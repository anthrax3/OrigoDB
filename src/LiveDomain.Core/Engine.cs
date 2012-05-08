﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Threading;
using LiveDomain.Core.Configuration;
using LiveDomain.Core.Logging;
using LiveDomain.Core.Security;

namespace LiveDomain.Core
{


    /// <summary>
    /// Engine is responsible for executing commands and queries against
    /// the model while conforming to ACID.
    /// </summary>
	public class Engine : IDisposable
    {

        /// <summary>
        /// The prevalent system. The database. Your single aggregate root. The graph.
        /// </summary>
        Model _theModel;

        /// <summary>
        /// All configuration settings, cloned in the constructor
        /// </summary>
        EngineConfiguration _config;
        IStorage _storage;
        ILockStrategy _lock;
        ISerializer _serializer;
        bool _isDisposed = false;
        ICommandJournal _commandJournal;
        static ILog _log = LiveDbConfiguration.Current.GetLogFactory().GetLogForCallingType();
        IAuthorizer<Type> _authorizer;

        private IAuthorizer<Type> CreateAuthorizer()
        {
            return _theModel as IAuthorizer<Type> ?? _config.CreateAuthorizer();
        }
 
        /// <summary>
        /// Shuts down the engine
        /// </summary>
        public void Close()
        {
            if (!_isDisposed)
            {
                if (_config.SnapshotBehavior == SnapshotBehavior.OnShutdown)
                {
                    //Allow reading while snapshot is being taken
                    //but no modifications after that
                    _lock.EnterUpgrade(); 
                    CreateSnapshotImpl("auto");

                }
                _lock.EnterWrite();
                _isDisposed = true;
                _commandJournal.Close();        
                    
            }
        }


        private void Restore<M>(Func<M> constructor) where M : Model
        {

            JournalSegmentInfo segment;

            _theModel = _storage.GetMostRecentSnapshot(out segment);

            if (_theModel == null) 
            {
                if(constructor == null)  throw new ApplicationException("No initial snapshot");
                _theModel = constructor.Invoke();
            }
            
            _theModel.SnapshotRestored();
            foreach (var command in _commandJournal.GetEntriesFrom(segment).Select(entry => entry.Item))
            {
                command.Redo(_theModel);
            }
            _theModel.JournalRestored();
        }


        private void ThrowIfDisposed()
        {
            if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);
        }


        protected Engine(Func<Model> constructor, EngineConfiguration config)
        {
            _serializer = config.CreateSerializer();
            
            //Prevent modification from outside
            _config = _serializer.Clone(config);

            _storage = _config.CreateStorage();
            _lock = _config.CreateLockingStrategy();

            _commandJournal = _config.CreateCommandJournal(_storage);
            Restore(constructor);
            _authorizer = CreateAuthorizer();
            _commandJournal.Open();
            
            if (_config.SnapshotBehavior == SnapshotBehavior.AfterRestore)
            {
                _log.Info("Starting snaphot job on threadpool");
                
                ThreadPool.QueueUserWorkItem((o) => CreateSnapshot("auto"));

                //Give the snapshot thread a chance to start and aquire the readlock
                Thread.Sleep(TimeSpan.FromMilliseconds(10));
            }
        }


        internal byte[] GetSnapshot()
        {
            try
            {
                _lock.EnterRead();
                return _serializer.Serialize(_theModel);
            }
            finally
            {
                _lock.Exit();
            }
        }

        internal void WriteSnapshotToStream(Stream stream)
        {
            try
            {
                _lock.EnterRead();
                _serializer.Write(_theModel, stream);
            }
            finally
            {
                _lock.Exit();
            }
        }

        #region Execute overloads
        
        public object Execute(Query query)
        {
            ThrowIfDisposed();
            ThrowUnlessAuthenticated(query.GetType());
        	return ExecuteQuery<Model, object>(model => query.ExecuteStub(model));
        }

        public T Execute<M,T>(Func<M,T> query) where M : Model
        {
            ThrowIfDisposed();
            ThrowUnlessAuthenticated(query.GetType());
            return ExecuteQuery(query);

        }
		private T ExecuteQuery<M, T>(Func<M, T> query) where M : Model
        {
            try
            {
                _lock.EnterRead();
                object result = query.Invoke(_theModel as M);
                if (_config.CloneResults && result != null) result = _serializer.Clone(result);
                return (T) result;
            }
            catch (TimeoutException)
            {
                ThrowIfDisposed();
                throw;
            }
            finally
            {
                _lock.Exit();
            }
        }

        public object Execute(Command command)
        {
            ThrowIfDisposed();
            ThrowUnlessAuthenticated(command.GetType());
            Command commandToSerialize = command;
            if (_config.CloneCommands) command = _serializer.Clone(command);
            
            try
            {
                _lock.EnterUpgrade();
                command.PrepareStub(_theModel);
                _lock.EnterWrite();
                object result = command.ExecuteStub(_theModel);
                if (_config.CloneResults && result != null) result = _serializer.Clone(result);
                _commandJournal.Append(commandToSerialize);
                return result;
            }
            catch (TimeoutException)
            {
                ThrowIfDisposed();
                throw; 
            }
            catch (CommandFailedException) { throw; }
            catch (Exception ex) 
            {
                Restore(() => (Model)Activator.CreateInstance(_theModel.GetType()));
                throw new CommandFailedException("Command threw an exception, state was rolled back, see inner exception for details", ex);
            }
            finally
            {
                _lock.Exit();
            }
        }

        private void ThrowUnlessAuthenticated(Type transactionType)
        {
            
            if (!_authorizer.Allows(transactionType, Thread.CurrentPrincipal))
            {
                var msg = String.Format("Access denied to type {0}", transactionType);
                throw new UnauthorizedAccessException(msg);
            }
        }

        #endregion

        #region Snapshot methods
        public void CreateSnapshot()
        {
            CreateSnapshot(String.Empty);
        }

        public void CreateSnapshot(string name)
        {
            try
            {
                _lock.EnterRead();
                CreateSnapshotImpl(name);
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                _lock.Exit();
            }
        }

        private void CreateSnapshotImpl(string name)
        {
            _log.Info("BeginSnapshot:" + name);
            _storage.WriteSnapshot(_theModel, name);
            _commandJournal.CreateNextSegment();
            _log.Info("EndSnapshot:" + name);
        }

        #endregion

        public void Dispose()
        {
            this.Close();
        }


        #region Static non-generic Load and Create methods

        public static Engine Load(string location)
        {
            return Load(new EngineConfiguration(location));
        }

        public static Engine Load(EngineConfiguration config)
        {
            if (!config.HasLocation) throw new InvalidOperationException("Specify location to load from in non-generic load");
            config.CreateStorage().VerifyCanLoad();
            var engine = new Engine(null, config);
            return engine;
        }

        public static Engine Create(Model model, string location)
        {
            return Create(model, new EngineConfiguration(location));
        }

        public static Engine Create(Model model, EngineConfiguration config)
        {
            if (!config.HasLocation) config.SetDefaultLocation(model.GetType());
            return Create<Model>(model, config);

        }
        

        #endregion
        
        #region Static generic Load methods

        /// <summary>
        /// Load using default configuration and location
        /// </summary>
        /// <returns>A strongly typed generic Engine</returns>
        public static Engine<M> Load<M>() where M : Model
        {
            return Load<M>(new EngineConfiguration());
        }

        /// <summary>
        /// Load from location using the default EngineConfiguration
        /// </summary>
        /// <typeparam name="M"></typeparam>
        /// <param name="location"></param>
        /// <returns></returns>
        public static Engine<M> Load<M>(string location) where M : Model
    	{
    		return Load<M>(new EngineConfiguration(location));
    	}

        /// <summary>
        /// Load using an explicit configuration.
        /// </summary>
        /// <typeparam name="M"></typeparam>
        /// <param name="config"></param>
        /// <returns></returns>
    	public static Engine<M> Load<M>(EngineConfiguration config) where M : Model
    	{
            if (!config.HasLocation) config.SetDefaultLocation<M>();
            config.CreateStorage().VerifyCanLoad();
			var engine = new Engine<M>(config);
    		return engine;
    	}
        #endregion

        #region Generic Create methods

        /// <summary>
        /// Create using default constructor, default EngineConfiguration at the default location
        /// </summary>
        /// <typeparam name="M"></typeparam>
        /// <returns></returns>
        public static Engine<M> Create<M>() where M : Model
        {
            return Create<M>(new EngineConfiguration());
        }

        public static Engine<M> Create<M>(string location) where M : Model
        {
            return Create<M>(new EngineConfiguration(location));
        }

        public static Engine<M> Create<M>(M model, string location) where M : Model
        {
            return Create<M>(model, new EngineConfiguration(location));
        }

        public static Engine<M> Create<M>(EngineConfiguration config) where M : Model
        {
            M model = Activator.CreateInstance<M>();
            return Create(model, config);
        }

        public static Engine<M> Create<M>(M model, EngineConfiguration config) where M : Model
        {
            if (!config.HasLocation) config.SetDefaultLocation<M>();
            IStorage storage = config.CreateStorage();
            storage.Create(model);
            return Load<M>(config);
        }

        #endregion

        #region Static generic LoadOrCreate methods


        public static Engine<M> LoadOrCreate<M>() where M : Model, new()
        {
            return LoadOrCreate<M>(new EngineConfiguration());
        }


        public static Engine<M> LoadOrCreate<M>(string location) where M : Model, new()
        {
            EngineConfiguration config = new EngineConfiguration(location);
            return LoadOrCreate<M>(config);
        }

        public static Engine<M> LoadOrCreate<M>(EngineConfiguration config) where M : Model, new()
        {

            Func<M> constructor = () => Activator.CreateInstance<M>();
            return LoadOrCreate<M>(constructor, config);
        }

        public static Engine<M> LoadOrCreate<M>(Func<M> constructor) where M : Model
        {
            return LoadOrCreate(constructor, new EngineConfiguration());
        }

        public static Engine<M> LoadOrCreate<M>(Func<M> constructor, EngineConfiguration config) where M : Model
        {
            if (constructor == null) throw new ArgumentNullException("constructor");
            if(config == null) throw new ArgumentNullException("config");
            if (!config.HasLocation) config.SetDefaultLocation<M>();
            Engine<M> result = null;

            var storage = config.CreateStorage();

            if (storage.Exists)
            {
                result = Load<M>(config);
                _log.Trace("Engine Loaded");
            }
            else if (storage.CanCreate)
            {
                result = Create<M>(constructor.Invoke(), config);
                _log.Trace("Engine Created");
            }
            else throw new ApplicationException("Couldn't load or create");
            return result;
        }

        #endregion
    }
}
