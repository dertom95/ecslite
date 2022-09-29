// ----------------------------------------------------------------------------
// The MIT License
// Threads support for LeoECS Lite https://github.com/Leopotam/ecslite-threads
// Copyright (c) 2021 Leopotam <leopotam@gmail.com>
// ----------------------------------------------------------------------------

namespace Leopotam.EcsLite.Threads
{

    public abstract class EcsThreadSystem<TThread,FilterType> : EcsThreadSystemBase, IEcsRunSystem
        where TThread : struct, IEcsThread where FilterType : IFilterData
    {
        EcsFilter<FilterType> _filter;
        TThread _thread;
        ThreadWorkerHandler _worker;
        System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();
        public virtual void BeforeWorkers(EcsSystems systems) { }
        public virtual void AfterWorkers(EcsSystems systems) { }

        public void Run(EcsSystems systems,float dt=0)
        {
            watch.Restart();

            if (_filter == null) {
                var world = GetWorld(systems);
                _filter = GetFilter<FilterType>(world);
                _thread = new TThread();
                _worker = Execute;
            }
            _thread.Init(_filter.GetRawEntities(), this);
            SetData(systems, ref _thread);
            ThreadService.Run(_worker, _filter.GetEntitiesCount(), GetChunkSize(systems));
            AfterWorkers(systems);
            watch.Stop();
            double time = watch.Elapsed.Milliseconds;
            //LeoTest.Urho3DSystem.I.DebugInfo($"System [{GetType().Name}] took {time}ms");
        }

        void Execute(int fromIndex, int beforeIndex)
        {
            _thread.Execute(fromIndex, beforeIndex);
        }

        protected virtual void SetData(EcsSystems systems, ref TThread thread) { }
    }




    public abstract class EcsThreadSystemBase
    {
        protected abstract int GetChunkSize(EcsSystems systems);
        protected abstract EcsFilter<FilterType> GetFilter<FilterType>(EcsWorld world) where FilterType:IFilterData ;
        protected abstract EcsWorld GetWorld(EcsSystems systems);
    }

    public interface IEcsThreadBase
    {
        void Execute(int fromIndex, int beforeIndex);
    }

    public interface IEcsThread : IEcsThreadBase
    {
        void Init(int[] entities, EcsThreadSystemBase system);
    }
}



