using System;
using UnityEngine;
using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;

namespace SoftOcclusionCulling
{
    [BurstCompile]
    public struct VertexShadingJob : IJobParallelFor
    {
        [ReadOnly]
        public NativeArray<Vector3> positionData;

        public Matrix4x4 mvpMat;
        
        public NativeArray<VSOutBuf> result;

        public void Execute(int index)
        {            
            var vert = positionData[index];
            var output = result[index];

            var objVert = new Vector4(vert.x, vert.y, -vert.z, 1);
            output.clipPos = mvpMat * objVert;
            result[index] = output;
        }
    }
}