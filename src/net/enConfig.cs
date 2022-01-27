using System;
using NetworkLayer;
using MessagePack;
using System.Collections.Generic;

namespace Leopotam.EcsLite.Net
{

    public class NetMQServerManagerConfig : EcsNetServerManagerConfiguration
    {
        public class NetMQMetaData {
            public int port;

            public NetMQMetaData(int port)
            {
                this.port = port;
            }
        }

        int portRangeFrom;
        int portRangeTo;
        int nextPort;


        public NetMQServerManagerConfig(int portRangeFrom,int portRangeTo=-1){
            this.portRangeFrom=portRangeFrom;
            this.portRangeTo=portRangeTo==-1?portRangeFrom:portRangeTo;
            this.nextPort=portRangeFrom;
        }

        private int NextPort(){
            if (nextPort>portRangeTo){
                throw new Exception($"portRange exceeded:{nextPort} > {portRangeTo}");
            }
            int result = nextPort++;
            return result;
        } 

        public override EcsServerInstance CreateServerInstance(EcsWorld world)
        {
            var port = NextPort();
            var netMQServer = new NetworkLayer.NetMQ.NetMQServer(port);
            var newServerInstance = new EcsServerInstance(netMQServer,world, new NetMQMetaData(port));
            return newServerInstance;
        }

        public override bool IsAllowedToCreateServerInstance()
        {
            return nextPort<=portRangeTo;
        }
    }

}