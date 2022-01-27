using System;
using System.Collections.Generic;

namespace Leopotam.EcsLite.Net
{
    public interface IServerInstanceCreator
    {
    }

    public abstract class EcsNetServerManagerConfiguration
    {
        public abstract bool IsAllowedToCreateServerInstance();
        public abstract EcsServerInstance CreateServerInstance(EcsWorld world);

        public virtual bool CheckValidity()
        {
            return true;
        }
    }

    public class EcsNetServerManager
    {
        public enum ServerManagerState
        {
            invalid,not_started, valid_configured, running, stopped
        }
        private HashSet<int> sendablePool = new HashSet<int>();
        private HashSet<int> saveablePool = new HashSet<int>();

        private EcsNetServerManagerConfiguration serverConfig;

        Dictionary<EcsWorld, EcsServerInstance> worldDataMapping = new Dictionary<EcsWorld, EcsServerInstance>();


        public ServerManagerState State { get; private set; } = ServerManagerState.not_started;

        public EcsNetServerManager(EcsNetServerManagerConfiguration configuration)
        {
            Configure(configuration);
        }

        /// <summary>
        /// Configure EcsNet by passing CreationCalls for IServer and/or IClient
        /// </summary>
        /// <param name="createServer"></param>
        /// <param name="createClient"></param>
        public bool Configure(EcsNetServerManagerConfiguration configuration)
        {
            this.serverConfig = configuration;
            
            if (worldDataMapping.Count > 0)
            {
                throw new Exception("EcsNet needs to be configured, BEFORE adding Worlds");
            }

            bool success = serverConfig.CheckValidity();
            if (!success)
            {
                State = ServerManagerState.invalid;
                return false;
            }

            State = ServerManagerState.valid_configured;
            return true;
        }

        private void CheckValidity(ServerManagerState checkState,string text = null)
        {
            if (State < checkState)
            {
                throw new Exception(text != null ? text : "Invalid state");
            }
        }

        public bool IsAllowedToCreateInstance(){
            return serverConfig.IsAllowedToCreateServerInstance();
        }

        public void Start(){
            CheckValidity(ServerManagerState.valid_configured,"Cannot Addworld in not valid state!");
            State = ServerManagerState.running;
        }

        public EcsServerInstance CreateInstance(EcsWorld world)
        {
            CheckValidity(ServerManagerState.valid_configured,"Cannot Addworld in not valid state!");
            if (worldDataMapping.TryGetValue(world, out EcsServerInstance data))
            {
                return data;
            }
            data = serverConfig.CreateServerInstance(world);
            worldDataMapping[world] = data;
            return data;
        }

        public void StopAll(){
            // TODO
        }


        public void Send()
        {
            foreach (var kv in worldDataMapping)
            {
                kv.Value.Send();
            }
        }

    }
}
