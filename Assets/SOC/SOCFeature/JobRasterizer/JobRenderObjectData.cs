using UnityEngine;
using Unity.Collections;

namespace SoftOcclusionCulling
{
    public class JobRenderObjectData : IRenderObjectData
    {

        public NativeArray<Vector3> positionData;
        public NativeArray<Vector3> normalData;
        public NativeArray<Vector2> uvData;
        public NativeArray<Vector3Int> trianglesData;
        
        public NativeArray<Vector3> boundsData;
        public NativeArray<Vector3Int> boundstrianglesData;

        public Vector3 lossyScale;
        public Vector3 eulerAngles;
        public Vector3 position;
        
        public JobRenderObjectData(Mesh mesh)
        {
            positionData = new NativeArray<Vector3>(mesh.vertexCount, Allocator.Persistent);
            positionData.CopyFrom(mesh.vertices);

            normalData = new NativeArray<Vector3>(mesh.vertexCount, Allocator.Persistent);
            normalData.CopyFrom(mesh.normals);

            uvData = new NativeArray<Vector2>(mesh.vertexCount, Allocator.Persistent);
            uvData.CopyFrom(mesh.uv);

            //初始化三角形数组，每个三角形包含3个索引值
            //注意这儿对调了v0和v1的索引，因为原来的 0,1,2是顺时针的，对调后是 1,0,2是逆时针的
            //Unity Quard模型的两个三角形索引分别是 0,3,1,3,0,2 转换后为 3,0,1,0,3,2
            var mesh_triangles = mesh.triangles;
            int triCnt = mesh_triangles.Length / 3;
            trianglesData = new NativeArray<Vector3Int>(triCnt, Allocator.Persistent);
            for(int i=0; i < triCnt; ++i){
                int j = i * 3;
                trianglesData[i] = new Vector3Int(mesh_triangles[j+1], mesh_triangles[j], mesh_triangles[j+2]);
            }

            // AABB
            boundsData = new NativeArray<Vector3>(24, Allocator.Persistent);
            boundsData.CopyFrom(GetBounds(mesh.bounds));
            
            int[] bounds_triangles = new[]
            {
                0, 2, 3, 0, 3, 1, 8, 4, 5, 8, 
                5, 9,10, 6, 7,10, 7,11,12,13,
                14,12,14,15,16,17,18,16,18,19,
                20,21,22,20,22,23
            };
            boundstrianglesData = new NativeArray<Vector3Int>(12, Allocator.Persistent);
            for(int i=0; i < 12; ++i){
                int j = i * 3;
                boundstrianglesData[i] = new Vector3Int(bounds_triangles[j+1], bounds_triangles[j], bounds_triangles[j+2]);
            }
        }

        public static Vector3[] GetBounds(Bounds localAABB)
        {
            Vector3[] bounds = new Vector3[24];
            var min = localAABB.min;
            var max = localAABB.max;
            bounds[0] = new Vector3(max.x, min.y, max.z);
            bounds[1] = new Vector3(min.x, min.y, max.z);
            bounds[2] = new Vector3(max.x, max.y, max.z);
            bounds[3] = new Vector3(min.x, max.y, max.z);
            bounds[4] = new Vector3(max.x, max.y, min.z);
            bounds[5] = new Vector3(min.x, max.y, min.z);
            bounds[6] = new Vector3(max.x, min.y, min.z);
            bounds[7] = new Vector3(min.x, min.y, min.z);
            
            bounds[8] = new Vector3(max.x, max.y, max.z);
            bounds[9] = new Vector3(min.x, max.y, max.z);
            bounds[10] = new Vector3(max.x, max.y, min.z);
            bounds[11] = new Vector3(min.x, max.y, min.z);
            bounds[12] = new Vector3(max.x, min.y, min.z);
            bounds[13] = new Vector3(max.x, min.y, max.z);
            bounds[14] = new Vector3(min.x, min.y, max.z);
            bounds[15] = new Vector3(min.x, min.y, min.z);
            
            bounds[16] = new Vector3(min.x, min.y, max.z);
            bounds[17] = new Vector3(min.x, max.y, max.z);
            bounds[18] = new Vector3(min.x, max.y, min.z);
            bounds[19] = new Vector3(min.x, min.y, min.z);
            bounds[20] = new Vector3(max.x, min.y, min.z);
            bounds[21] = new Vector3(max.x, max.y, min.z);
            bounds[22] = new Vector3(max.x, max.y, max.z);
            bounds[23] = new Vector3(max.x, min.y, max.z);
            
            return bounds;
        }
        ~JobRenderObjectData()
        {
            if(positionData.IsCreated) positionData.Dispose();  
            if(normalData.IsCreated) normalData.Dispose();
            if(uvData.IsCreated) uvData.Dispose();
            if(trianglesData.IsCreated) trianglesData.Dispose();
            if(boundsData.IsCreated) boundsData.Dispose();   
            if(boundstrianglesData.IsCreated) boundstrianglesData.Dispose();   
            Debug.Log("~JobRenderObjectData.Release");
        }
        
        public void Release()
        {
            if(positionData.IsCreated) positionData.Dispose();  
            if(normalData.IsCreated) normalData.Dispose();
            if(uvData.IsCreated) uvData.Dispose();
            if(trianglesData.IsCreated) trianglesData.Dispose();   
            if(boundsData.IsCreated) boundsData.Dispose();   
            if(boundstrianglesData.IsCreated) boundstrianglesData.Dispose();   
            Debug.Log("JobRenderObjectData.Release");
        }

    }
}

