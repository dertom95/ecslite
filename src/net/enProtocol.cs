using System;
using NetworkLayer;
using MessagePack;
using System.Collections.Generic;

namespace Leopotam.EcsLite.Net
{

    /// <summary>
    /// Interface to ensure this element has an entity field, that can be set(e.g. when this component is attached to an entity)
    /// </summary>
    public interface IWithEntityID
    {
        int Entity { get; set; }
    }

    public interface IProtocol
    {
        public const int MSG_ECS_NET_COMPONENT_CHANGED = 1100;
        public const int MSG_ECS_NET_COMPONENT_REMOVED = 1101;

        public const int MSG_ECS_NET_DATA_FROM_CLIENT = 1102;
    }

    /// <summary>
    /// Decorator interface to indicate that this element should be send over the wire
    /// </summary>
    public interface IEcsSendableFromServer { };
    public interface IEcsSendableFromClient { };
    public interface IEcsSavable { };

    [MessagePackObject]
    public struct MSGEntityChanged
    {
        [Key(0)]
        public int entityId;
        [Key(1)]
        public int poolId;
        [Key(2)]
        public bool added;
    }
}