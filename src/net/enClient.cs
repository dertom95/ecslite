namespace Leopotam.EcsLite.Net
{
    using System;
    using NetworkLayer;
    using MessagePack;
    using System.Collections.Generic;


    public class ClientUserContext {
        public EcsNetClientInstance instance;
        public IClient client;
        public EcsWorld world;

        public ClientUserContext(EcsNetClientInstance instance,IClient client, EcsWorld world)
        {
            this.instance = instance;
            this.client = client;
            this.world = world;
        }
    }


    public class EcsNetClientInstance {
        protected IClient client;
        public IClient Client => client;

        protected EcsWorld world;
        
        protected virtual ClientUserContext CreateUserData(){
            return new ClientUserContext(this,client,world);
        }

        /// <summary>
        /// Userobject passed to every onReceive call
        /// </summary>
        /// <value></value>
        protected ClientUserContext ClientContext {get;private set;}

        public Func<IConnection,int,int,LinkedList<byte[]>,ClientUserContext,bool> OnReceive;

        protected bool forwardInternalMessages = false;

        public EcsNetClientInstance(IClient client,EcsWorld world,bool forwardInternalMessages=false){
            this.world = world;
            this.client = client;
            this.forwardInternalMessages = forwardInternalMessages;
            ClientContext = CreateUserData();
        }

        public void Start(bool cacheIncomingData=false){
            client.OnReceive += HandleClientReceive;   
            if (forwardInternalMessages){
                client.OnInternalReceive += HandleClientReceive;
            }         
            client.Connect(cacheIncomingData);
        }


        /// <summary>
        /// Incoming data from the server. e.g. new-entities, component-changed, ....
        /// </summary>
        /// <param name="con"></param>
        /// <param name="dataId"></param>
        /// <param name="dataValue"></param>
        /// <param name="frames"></param>
        /// <returns></returns>
        protected bool HandleClientReceive(IConnection con,int dataId,int dataValue,LinkedList<byte[]> frames){
            bool handled = false;
            switch(dataId){
                case IProtocol.MSG_ECS_NET_COMPONENT_CHANGED: 
                    byte[] frame =frames.PopFrame();
                    var msgChanged = MessagePackSerializer.Deserialize<MSGEntityChanged>(frame);
                    var pool = world.GetPoolById(msgChanged.poolId);

                    if (msgChanged.added){
                        if (!world.IsEntityAliveInternal(msgChanged.entityId)){
                            world.NewEntity(msgChanged.entityId);
                        }
                        var component = MessagePackSerializer.Deserialize(pool.GetComponentType(),frames.PopFrame());
                        if (pool.Has(msgChanged.entityId)){
                            pool.SetRaw(msgChanged.entityId,component);
                        } else {
                            pool.AddRaw(msgChanged.entityId,component);
                        }
                        handled = true;
                    } else {
                        if (world.IsEntityAliveInternal(msgChanged.entityId) && pool.Has(msgChanged.entityId)){
                            pool.Del(msgChanged.entityId);
                        }
                        handled = true;
                    }

                    break;
            }

            if (handled){
                return true;
            }

            if (OnReceive!=null){
                handled = OnReceive(con,dataId,dataValue,frames,ClientContext);
            }

            return handled;
        }

    }


}
