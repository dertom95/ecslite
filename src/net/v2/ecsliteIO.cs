using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using NetworkLayer2;

namespace Leopotam.EcsLite.Net2
{


    [MessagePackObject]
    public class EcsMSGComponentChanged
    {
        [Key(0)]
        public int entityId;
        [Key(1)]
        public int componentId;
        [Key(2)]
        public byte[] componentPayload;
    }

    [MessagePackObject]
    public class EcsMsgComponentRemoved
    {
        [Key(0)]
        public int entityId;
        [Key(1)]
        public int componentId;

    }

    [MessagePackObject]
    public class EcsMsgEntitiesRemoved
    {
        [Key(0)]
        public List<int> newEntityIds;
    }

    [MessagePackObject]
    public class EcsMsgLogin
    {
        [Key(0)]
        public string username;
        [Key(1)]
        public string password;
    }


    public interface IEcsProtocol {
        public const int MSG_COMPONENT_CHANGED=1000;
        public const int MSG_COMPONENT_REMOVED=1001;
        public const int MSG_ENTITIES_REMOVED=1002;
        public const int MSG_LOGIN=1003;
    }

    public class EcsIO : IEcsWorldEventListener
    {
        /// <summary>
        /// Receive messages that are unknown to the ecs-system
        /// </summary>
        /// <value></value>
        public Action<int,byte[],IOIdentity> OnUnknownMessage {
            get=>msgIO.OnUnknownMessage;
            set=>msgIO.OnUnknownMessage=value;
        }
        
        /// <summary>
        /// Base receiver/connection
        /// </summary>
        private InputOutput io;

        private BufferIncomingIO bufferIO;
        /// <summary>
        /// Base Server functions
        /// </summary>
        public BaseServerIO serverIO;
        /// <summary>
        /// Highlevel MessageHandling (MsgPack-Serialization)
        /// </summary>
        private MessageInputOutput msgIO;        

        /// <summary>
        /// this 
        /// </summary>
        /// <typeparam name="int"></typeparam>
        /// <returns></returns>
        private HashSet<int> acceptIncoming = new HashSet<int>();
        
        /// <summary>
        /// this componentIds will be marked for send(outgoing)
        /// </summary>
        /// <typeparam name="int"></typeparam>
        /// <returns></returns>
        private HashSet<int> acceptOutgoing = new HashSet<int>();
        
        /// <summary>
        /// store changed components as key-value-pair of entityId and poolId
        /// </summary>
        public HashSet<KeyValuePair<int,int>> dirtyComponents = new HashSet<KeyValuePair<int, int>>();
 
        /// <summary>
        /// store removed components as key-value-pair of entityId and poolId
        /// </summary>
        public HashSet<KeyValuePair<int,int>> removedComponents = new HashSet<KeyValuePair<int, int>>();




        protected EcsWorld world;

        private int counter = 0;

        public bool IsDisposed {get;private set;}=false;

        private Thread serverThread;
        private bool serverRunning=false;

        public bool ServerMode {get;private set;}

        public struct CheckIncomingData{
            public bool addedOrChanged; // if false => removed
            public int entityId;
            public int componentId;
            public object componentObject;
            public IOIdentity identity;
        }
        public Func<CheckIncomingData,bool> CheckIncomingComponent{get;set;}
        public Action<int,object,IOIdentity> OnIncomingCommandMessage{get;set;}
        /// <summary>
        /// Only for servermode
        /// </summary>
        /// <value></value>
        public Func<IOIdentity,int,byte[],object> CheckNewUser{
            get=>serverIO==null?null:serverIO.CheckNewUser;
                
            set{
                if (serverIO!=null){
                    serverIO.CheckNewUser=value;
                }
            }
        }

        public EcsIO(EcsWorld world,bool serverMode, params InputOutput[] inputs)
        {
            ServerMode = serverMode;
            if (inputs == null || inputs.Length == 0)
            {
                throw new System.Exception("you need to specify at least one inputIO");
            }

            this.world = world;
            world.SetEventListener(this);

            if (inputs.Length==1){
                io = inputs[0];
            } else {
                io = new MuxIO(inputs);
            }
            bufferIO = new BufferIncomingIO(io);         // add layer to block incoming datastream
            
            if (serverMode){
                serverIO = new BaseServerIO(bufferIO); // wrap base-server logic over incoming data-streams
                msgIO = new MessageInputOutput(serverIO); // wrap highlevel-message-handling over server to automatically receive specific messages
            } else {
                msgIO = new MessageInputOutput(bufferIO);
            }
        
            // register valid messages
            msgIO.AddMapping(IEcsProtocol.MSG_COMPONENT_CHANGED,typeof(EcsMSGComponentChanged));
            msgIO.AddMapping(IEcsProtocol.MSG_COMPONENT_REMOVED,typeof(EcsMsgComponentRemoved));
            msgIO.AddMapping(IEcsProtocol.MSG_ENTITIES_REMOVED,typeof(EcsMsgEntitiesRemoved));
// doesn't come through server
            msgIO.AddMapping(IEcsProtocol.MSG_LOGIN,typeof(EcsMsgLogin)); 
        }

        private void ProcessIncomingChangedComponent(EcsMSGComponentChanged msg,IOIdentity identity){
            if (!acceptIncoming.Contains(msg.componentId)){
                // TODO: add some kind of callback!?
                return;
            }

            var pool = world.GetPoolById(msg.componentId);
            var compData = (IWithEntityID) MessagePackSerializer.Deserialize(pool.GetComponentType(),msg.componentPayload);
            
            bool proceed = true;
            if (CheckIncomingComponent!=null){
                proceed = CheckIncomingComponent(new CheckIncomingData(){
                    addedOrChanged=true,
                    entityId=msg.entityId,
                    componentId=msg.componentId,
                    componentObject=compData,
                    identity=identity
                });
            }
            if (!proceed){
                return;
            }

            ApplyComponent(msg.entityId,msg.componentId,compData);
        }

        private void ProcessIncomingRemovedComponent(EcsMsgComponentRemoved msg,IOIdentity identity){
            if (!acceptIncoming.Contains(msg.componentId)){
                // TODO: add some kind of callback!?
                return;
            }

            bool proceed = true;
            if (CheckIncomingComponent!=null){
                proceed = CheckIncomingComponent(new CheckIncomingData(){
                    addedOrChanged=false, // removed
                    entityId=msg.entityId,
                    componentId=msg.componentId,
                    componentObject=null,
                    identity=identity
                });
            }
            if (!proceed){
                return;
            }


            RemoveComponent(msg.entityId,msg.componentId);
        }

        public void Start(){
            if (serverRunning){
                throw new Exception("Server already running");
            }
            serverRunning=true;
            serverThread = new Thread(ThreadLogic);
            serverThread.IsBackground=true;
            serverThread.Name="ecs-net-thread";
            serverThread.Start();
        }

        public void Stop(){
            if (!serverRunning){
                throw new Exception("Trying to stop server,but server not running!");
            }
            serverRunning = false;
            // TODO: send dummy message to be user the loop could execute!??
        }

        private void ThreadLogic(){
            try{
                while (serverRunning){
                    var (msgId,msgObject,identity) = msgIO.ReadMessage();

                    switch(msgId){
                        case IEcsProtocol.MSG_COMPONENT_CHANGED:
                            ProcessIncomingChangedComponent((EcsMSGComponentChanged)msgObject,identity);
                            break;
                        case IEcsProtocol.MSG_COMPONENT_REMOVED:
                            ProcessIncomingRemovedComponent((EcsMsgComponentRemoved)msgObject,identity);
                            break;
                        case IEcsProtocol.MSG_ENTITIES_REMOVED:
                            throw new Exception("MSG_ENTITIES_REMOVED not implemented,yet. (not sure it is needed");
                        default:
                            // everything else is considered to be a command
                            if (OnIncomingCommandMessage!=null){
                                OnIncomingCommandMessage(msgId,msgObject,identity);
                            }
                            Console.WriteLine($"[{identity}] Incoming: command-msg: id:{msgId}");
                            break;
                    }
                }
                int a=0;
            }
            catch (Exception e){
                Console.WriteLine(e);
            }
        }

        /// <summary>
        /// These components are accepted if incoming
        /// </summary>
        /// <param name="componentId"></param>
        /// <returns></returns>
        public EcsIO AcceptIncoming<T>() where T:struct,IWithEntityID{
            var pool = world.GetPool<T>();
            acceptIncoming.Add(pool.GetId());
            return this;
        }
        
        /// <summary>
        /// This component ids will be send
        /// </summary>
        /// <param name="componentId"></param>
        /// <returns></returns>
        public EcsIO AcceptOutgoing<T>() where T:struct,IWithEntityID{
            var pool = world.GetPool<T>();
            acceptOutgoing.Add(pool.GetId());
            return this;
        }
        public EcsIO RegisterCommand<T>(ushort msgId){
            return RegisterCommand(msgId,typeof(T));
        }

        public EcsIO RegisterCommand(ushort msgId,Type msgType){
            if (!msgIO.IsMappingIDAvailable(msgId)){
                throw new Exception("mapping-id already taken!");
            }
            msgIO.AddMapping(msgId,msgType);
            return this;
        }

        public void ApplyComponent(int entityId, int componentId, IWithEntityID componentObject)
        {
            var pool = world.GetPoolById(componentId);

            if (!world.IsEntityAliveInternal(entityId))
            {
                world.NewEntity(entityId);
                Console.WriteLine($"Client [{world}][{counter}]: newEntity:{entityId}");
            }

            if (pool.Has(entityId))
            {
                Console.WriteLine($"[{world}]: setRaw:entity[{counter}]:{entityId}|pid:{componentId}");
                pool.SetRaw(entityId, componentObject);
            }
            else
            {
                Console.WriteLine($"[{world}]: AddRaw:entity[{counter}]:{entityId}|pid:{componentId}");
                pool.AddRaw(entityId, componentObject);
            }
        }

        public void RemoveComponent(int entityId, int componentId){
            if (!world.IsEntityAliveInternal(entityId)){
                // TODO: some kind of feedback that this entity doesnt exist?
                return;
            }
            var pool = world.GetPoolById(componentId);
            
            if (pool.Has(entityId)){
                pool.Del(entityId);
            }
        }

        public void WriteRawMessage(ushort dataId,byte[] payload,IOIdentity identity){
            msgIO.WriteRawMessage(dataId,payload,identity);
        }

        public void WriteMessage<T>(T msg,IOIdentity ident=default){
            msgIO.WriteMessage<T>(msg,ident);
        }

        public void BroadcastRaw(ushort dataId,byte[] payload,IOIdentity? ignoreUser=null){
            if (ServerMode){
                serverIO.Broadcast(dataId,payload,ignoreUser);
            } else {
                throw new Exception("Client is not able to use broadcast");
            }
        }

        public void RemoveEntity(int entityId){
            if (!world.IsEntityAliveInternal(entityId)){
                // TODO: feedback? informative exception?
                return;
            }
            world.DelEntity(entityId);
        }

        public T GetUserData<T>(IOIdentity ident){
            return serverIO.GetUserObject<T>(ident);
        }

        public void SendECSData(){
            var msgChanged = new EcsMSGComponentChanged();
            foreach (var kv in dirtyComponents){
                msgChanged.entityId=kv.Key;
                msgChanged.componentId=kv.Value;

                var pool = world.GetPoolById(msgChanged.componentId);
                var component = pool.GetRaw(msgChanged.entityId); 
                msgChanged.componentPayload=MessagePackSerializer.Serialize(component);

                var (dataId,payload) = msgIO.CreateRawMessage(msgChanged);
                if (ServerMode){
                    serverIO.Broadcast(dataId,payload);   // server=>clients
                } else {
                    msgIO.WriteRawMessage(dataId,payload); // client=>server
                }
            }
            var msgRemoved = new EcsMsgComponentRemoved();
            foreach (var kv in removedComponents){
                msgRemoved.entityId=kv.Key;
                msgRemoved.componentId=kv.Value;
                
                var (dataId,payload) = msgIO.CreateRawMessage(msgRemoved);
                if (ServerMode){
                    serverIO.Broadcast(dataId,payload);   // server=>clients
                } else {
                    msgIO.WriteRawMessage(dataId,payload); // client=>server
                }
            }
            dirtyComponents.Clear();
            removedComponents.Clear();    
        }
        

        public void FlushIncoming(){
            bufferIO.Flush();
        }


        public void Dispose()
        {
            if (IsDisposed){
                throw new Exception("Already Disposed!");
            }
            Stop();
            msgIO.Dispose();
            IsDisposed=true;
        }

        public void OnEntityCreated(int entity)
        {
        }

        public void OnEntityChanged(int entity, int componentId, bool added)
        {
            if (acceptOutgoing.Contains(componentId)){
                if (added)
                {
                    MarkDirty(entity,componentId);
                }
                else
                {
                    var kv = new KeyValuePair<int,int>(entity,componentId);
                    dirtyComponents.Remove(kv);
                    removedComponents.Add(kv);
                }
            }
        }

        public void MarkDirty(int entityId,int componentId,bool force=false){
            if (force || acceptOutgoing.Contains(componentId)){
                var kv = new KeyValuePair<int,int>(entityId,componentId);
                dirtyComponents.Add(kv);
                removedComponents.Remove(kv);
            }
        }

        public void OnEntityDestroyed(int entity)
        {
            //TODO: Do I need this or is this covered by all components gone=>auto remove?
        }

        public void OnFilterCreated(EcsFilter filter)
        {
        }

        public void OnWorldResized(int newSize)
        {
        }

        public void OnWorldDestroyed(EcsWorld world)
        {
        }
    }

}