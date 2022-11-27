using System;
using UnityEngine;
using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;

namespace SoftOcclusionCulling
{
    [BurstCompile]
    public struct TriangleCullingJob : IJobParallelFor
    {
        [ReadOnly]
        public NativeArray<Vector3Int> trianglesData;  
        [ReadOnly]
        public NativeArray<Vector2> uvData; 
        [ReadOnly]
        public NativeArray<VSOutBuf> vsOutput;

        [NativeDisableParallelForRestriction]
        public NativeArray<Color> frameBuffer;
        [NativeDisableParallelForRestriction]
        public NativeArray<float> depthBuffer;
        
        // 判断遮挡
        [NativeDisableParallelForRestriction]
        public NativeArray<bool> NeedMoveToCullingLayer;
        public NativeArray<Vector4> clipAABB;
        public Vector4 minClip;
        public Vector4 maxClip;

        public int screenWidth;
        public int screenHeight; 

        [ReadOnly]
        public NativeArray<URColor24> TextureData;   
        public int TextureWidth;
        public int TextureHeight;  

        public bool UseBilinear;   

        public ShaderType fsType;  

        public ShaderUniforms Uniforms;
        
        // public NativeArray<Vector3> positionData;
        // public Matrix4x4 mvpMat;

        public void Execute(int index)
        {

            var pos = clipAABB[index];

            // ------ Clipping -------
            if (Clipped(pos))
            {
                return;
            }                

            // ------- Perspective division --------
            //clip space to NDC
            pos.x /= pos.w; pos.y /= pos.w; pos.z /= pos.w;

            //backface culling
            // {
            //     Vector3 t0 = new Vector3(pos0.x, pos0.y, pos0.z);
            //     Vector3 t1 = new Vector3(pos2.x, pos2.y, pos2.z);
            //     Vector3 t2 = new Vector3(pos6.x, pos6.y, pos6.z);
            //     Vector3 e01 = t1 - t0;
            //     Vector3 e02 = t2 - t0;
            //     Vector3 cross = Vector3.Cross(e01, e02);
            //     if (cross.z < 0)
            //     {
            //         return;
            //     }
            // }

            // ------- Viewport Transform ----------
            //NDC to screen space            
            {
                int max_w = screenWidth - 1;
                int max_h = screenHeight - 1;
                pos.x = 0.5f * max_w * (pos.x + 1.0f);
                pos.y = 0.5f * max_h * (pos.y + 1.0f);                
                pos.z = pos.z * 0.5f + 0.5f; 

                // v1.x = 0.5f * max_w * (v1.x + 1.0f);
                // v1.y = 0.5f * max_h * (v1.y + 1.0f);                
                // v1.z = v1.z * 0.5f + 0.5f; 
                //
                // v2.x = 0.5f * max_w * (v2.x + 1.0f);
                // v2.y = 0.5f * max_h * (v2.y + 1.0f);                
                // v2.z = v2.z * 0.5f + 0.5f;
            }

            Triangle t = new Triangle();
            t.Vertex0.Position = pos;
            // t.Vertex1.Position = v1;
            // t.Vertex2.Position = v2;

            RasterizeTriangle(t);
            
        }

        bool Clipped(Vector4 v0, Vector4 v1, Vector4 v2)
        {            
            //分别检查视锥体的六个面，如果三角形所有三个顶点都在某个面之外，则该三角形在视锥外，剔除  
            //由于NDC中总是满足-1<=Zndc<=1, 而当 w < 0 时，-w >= Zclip = Zndc*w >= w。所以此时clip space的坐标范围是[w,-w], 为了比较时更明确，将w取正      
            
            var w0 = v0.w >=0 ? v0.w : -v0.w;            
            var w1 = v1.w >=0 ? v1.w : -v1.w;            
            var w2 = v2.w >=0 ? v2.w : -v2.w;
            
            //left
            if(v0.x < -w0 && v1.x < -w1 && v2.x < -w2){
                return true;
            }
            //right
            if(v0.x > w0 && v1.x > w1 && v2.x > w2){
                return true;
            }
            //bottom
            if(v0.y < -w0 && v1.y < -w1 && v2.y < -w2){
                return true;
            }
            //top
            if(v0.y > w0 && v1.y > w1 && v2.y > w2){
                return true;
            }
            //near
            if(v0.z < -w0 && v1.z < -w1 && v2.z < -w2){
                return true;
            }
            //far
            if(v0.z > w0 && v1.z > w1 && v2.z > w2){
                return true;
            }
            return false;       
        }
        
        bool Clipped(Vector4 v0)
        {            
            //分别检查视锥体的六个面，如果顶点在某个面之外，则该顶点在视锥外，剔除  
            //由于NDC中总是满足-1<=Zndc<=1, 而当 w < 0 时，-w >= Zclip = Zndc*w >= w。所以此时clip space的坐标范围是[w,-w], 为了比较时更明确，将w取正      
            
            var w0 = v0.w >=0 ? v0.w : -v0.w;
            
            //left
            if(v0.x < -w0 ){
                return true;
            }
            //right
            if(v0.x > w0){
                return true;
            }
            //bottom
            if(v0.y < -w0 ){
                return true;
            }
            //top
            if(v0.y > w0){
                return true;
            }
            //near
            if(v0.z < -w0 ){
                return true;
            }
            //far
            if(v0.z > w0){
                return true;
            }
            return false;       
        }
        
        Vector3 ComputeBarycentric2D(float x, float y, Triangle t)
        {                      
            var v0 = t.Vertex0.Position;
            var v1 = t.Vertex1.Position;
            var v2 = t.Vertex2.Position;
            
            float c1 = (x * (v1.y - v2.y) + (v2.x - v1.x) * y + v1.x * v2.y - v2.x * v1.y) 
                       / (v0.x * (v1.y - v2.y) + (v2.x - v1.x) * v0.y + v1.x * v2.y - v2.x * v1.y);
            float c2 = (x * (v2.y - v0.y) + (v0.x - v2.x) * y + v2.x * v0.y - v0.x * v2.y) / (v1.x * (v2.y - v0.y) + (v0.x - v2.x) * v1.y + v2.x * v0.y - v0.x * v2.y);
            float c3 = (x * (v0.y - v1.y) + (v1.x - v0.x) * y + v0.x * v1.y - v1.x * v0.y) / (v2.x * (v0.y - v1.y) + (v1.x - v0.x) * v2.y + v0.x * v1.y - v1.x * v0.y);
            return new Vector3(c1, c2, c3);
        }
        
        public int GetIndex(int x, int y)
        {
            return y * screenWidth + x;
        }

        void RasterizeTriangle(Triangle t)
        {                        
            var v0 = t.Vertex0.Position;
            // var v1 = t.Vertex1.Position;
            // var v2 = t.Vertex2.Position;  
            
            int v0x = Mathf.CeilToInt(v0.x);
            v0x = v0x < 0 ? 0 : v0x;
            v0x = v0x > screenWidth ? screenWidth : v0x;
            int v0y = Mathf.CeilToInt(v0.y);
            v0y = v0y < 0 ? 0 : v0y;
            v0y = v0y > screenHeight ? screenHeight : v0y;
            
            int index = GetIndex(v0x, v0y);
            frameBuffer[index] = Color.white;
            if (minClip.z >= depthBuffer[index])
            {
                depthBuffer[index] = minClip.z;
                NeedMoveToCullingLayer[0] = false;
            }



            // //Find out the bounding box of current triangle.
            // float minX = (v0.x < v1.x) ? ((v0.x < v2.x)?v0.x : (v1.x < v2.x)?v1.x:v2.x) : ((v1.x < v2.x)?v1.x:(v0.x < v2.x)?v0.x:v2.x);
            // float maxX = (v0.x > v1.x) ? ((v0.x > v2.x)?v0.x : (v1.x > v2.x)?v1.x:v2.x) : ((v1.x > v2.x)?v1.x:(v0.x > v2.x)?v0.x:v2.x);
            // float minY = (v0.y < v1.y) ? ((v0.y < v2.y)?v0.y : (v1.y < v2.y)?v1.y:v2.y) : ((v1.y < v2.y)?v1.y:(v0.y < v2.y)?v0.y:v2.y);
            // float maxY = (v0.y > v1.y) ? ((v0.y > v2.y)?v0.y : (v1.y > v2.y)?v1.y:v2.y) : ((v1.y > v2.y)?v1.y:(v0.y > v2.y)?v0.y:v2.y);                        
            //                             
            //
            // int minPX = Mathf.FloorToInt(minX);
            // minPX = minPX < 0 ? 0 : minPX;
            // int maxPX = Mathf.CeilToInt(maxX);
            // maxPX = maxPX > screenWidth ? screenWidth : maxPX;
            // int minPY = Mathf.FloorToInt(minY);
            // minPY = minPY < 0 ? 0 : minPY;
            // int maxPY = Mathf.CeilToInt(maxY);
            // maxPY = maxPY > screenHeight ? screenHeight : maxPY;
            //
            // const float tolerant = -0.0005f;
            //
            // // 遍历当前三角形包围中的所有像素，判断当前像素是否在三角形中
            // // 对于在三角形中的像素，使用重心坐标插值得到深度值，并使用z buffer进行深度测试和写入
            // for (int y = minPY; y < maxPY; ++y)
            // {
            //     for(int x = minPX; x < maxPX; ++x)
            //     {
            //         //深度测试(注意我们这儿的z值越大越靠近near plane，因此大值通过测试）
            //         index = GetIndex(x, y);
            //         frameBuffer[index] = Color.white;
            //         if (minClip.z >= depthBuffer[index])
            //         {
            //             depthBuffer[index] = minClip.z;
            //             NeedMoveToCullingLayer[0] = false;
            //         }
            //     }
            // }                                    
        }
    }
}

