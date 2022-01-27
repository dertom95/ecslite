using System;
using System.Collections.Generic;
using MessagePack;
using NetworkLayer;

namespace Leopotam.EcsLite.Net
{
    /// <summary>
    /// This context is created for each world-instance and is returned on each callback
    /// This is just a base-class. To use a custom EcsWorld fitting to your use-case:
    /// - inherit from this class
    /// - register on EcsNet.I.RegisterContextType(typeof(YourEcsWorldContext))
    /// </summary>
    public class EcsServerInstanceContext {
        public EcsServerInstance serverInstance;
        public EcsWorld world;
        public IServer server;

        public EcsServerInstanceContext(EcsServerInstance serverInstance,EcsWorld world, IServer server)
        {
            this.serverInstance = serverInstance;
            this.world = world;
            this.server = server;
        }
    }

    public class EcsServerInstance : IEcsWorldEventListener
    {
        protected EcsWorld world;
        public EcsWorld World=>world;

        /// <summary>
        /// store changed components as key-value-pair of entityId and poolId
        /// </summary>
        protected HashSet<KeyValuePair<int,int>> dirtyComponents;
 
        /// <summary>
        /// store removed components as key-value-pair of entityId and poolId
        /// </summary>
        protected HashSet<KeyValuePair<int,int>> removedComponents;

        protected IServer server;
        public IServer Server => server;
        
        public object MetaData {get;private set;}
        protected EcsServerInstanceContext context;
        public EcsServerInstanceContext ServerContext => context;

        public Func<IConnection,int,int,LinkedList<byte[]>,EcsServerInstanceContext,bool> OnReceive;

        public EcsServerInstance(IServer server,EcsWorld world,object metaData=null)
        {
            this.server = server;
            this.world = world;

            dirtyComponents = new HashSet<KeyValuePair<int,int>>();
            removedComponents = new HashSet<KeyValuePair<int,int>>();

            context = CreateWorldContext();

            world.SetEventListener(this);

            MetaData = metaData;
        }
        
        public bool HandleReceive(IConnection con, int dataId, int dataValue, LinkedList<byte[]> pendingFrames){
            bool handled = false;
            if (OnReceive!=null){
                handled = OnReceive(con,dataId,dataValue,pendingFrames,context);
            }
            return handled;
        }   

        protected virtual EcsServerInstanceContext CreateWorldContext(){
            var context = new EcsServerInstanceContext(this,world,server);
            return context;
        }

        public T GetContext<T>() where T:EcsServerInstanceContext{
            return (T)context;
        }




        /// <summary>
        /// Starts the server
        /// </summary>
        public void Start(){
            server.Start();
        }



        public void MarkDirty(int entity, int poolId)
        {
            var kv = new KeyValuePair<int,int>(entity,poolId);
            removedComponents.Remove(kv);
            dirtyComponents.Add(kv);
        }

        public void OnEntityChanged(int entity, int poolId, bool added)
        {
            if (!world.IsSendablePool(poolId)){
                // ignore pools with components that are not supposed to be send over the wire
                return;
            }

            Console.WriteLine($"OnEntityChanged:{entity}|{poolId}|{added}");

            var kv = new KeyValuePair<int,int>(entity,poolId);
            if (added)
            {
                MarkDirty(entity,poolId);
            }
            else
            {
                dirtyComponents.Remove(kv);
                removedComponents.Add(kv);
            }
        }

        public void OnEntityCreated(int entity)
        {
        }

        public void OnEntityDestroyed(int entity)
        {
        }

        public void OnFilterCreated(EcsFilter filter)
        {
        }

        public void OnWorldDestroyed(EcsWorld world)
        {
            // TODO
        }

        public void OnWorldResized(int newSize)
        {
            Console.WriteLine("OnWorldResized");
        }

        public void Send()
        {
            MSGEntityChanged msg = new MSGEntityChanged();
            var frames = new LinkedList<byte[]>();
            
            foreach (var entComp in dirtyComponents){
                frames.Clear();
                var pool = world.GetPoolById(entComp.Value);
                var comp = pool.GetRaw(entComp.Key);
                
                msg.entityId=entComp.Key;
                msg.poolId=entComp.Value;
                msg.added=true;
                frames.PushEnvelope(IProtocol.MSG_ECS_NET_COMPONENT_CHANGED,0);
                byte[] entChangedMsg = MessagePackSerializer.Serialize(msg);
                byte[] payload = MessagePackSerializer.Serialize(comp);
                frames.PushFrame(entChangedMsg);
                frames.PushFrame(payload);
                server.Broadcast(frames,IServer.BROADCAST_CHANNEL_INTERNAL);
            }

            foreach (var entComp in removedComponents){
                frames.Clear();
                var pool = world.GetPoolById(entComp.Value);
                
                msg.entityId=entComp.Key;
                msg.poolId=entComp.Value;
                msg.added=false;

                frames.PushEnvelope(IProtocol.MSG_ECS_NET_COMPONENT_REMOVED,0);
                byte[] entChangedEnvelope = MessagePackSerializer.Serialize(msg);
                frames.PushFrame(entChangedEnvelope);
                server.Broadcast(frames,IServer.BROADCAST_CHANNEL_INTERNAL);
            } 
            frames.Clear();     
            removedComponents.Clear();
            dirtyComponents.Clear();      
        }
    }
}