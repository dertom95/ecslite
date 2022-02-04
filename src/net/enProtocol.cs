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

}