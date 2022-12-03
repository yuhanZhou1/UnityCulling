using System;
using UnityEngine;
using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;

namespace SoftOcclusionCulling
{
    [BurstCompile]
    public struct ObjectCullingJob : IJobParallelFor
    {
        
        [ReadOnly] public NativeArray<Vector3Int> boundstrianglesData;
        
        [NativeDisableParallelForRestriction]
        public NativeArray<Color> frameBuffer;
        [NativeDisableParallelForRestriction]
        public NativeArray<float> depthBuffer;
        
        // 判断遮挡
        [NativeDisableParallelForRestriction]
        public NativeArray<bool> NeedMoveToCullingLayer;
        // public Vector4 minClip;
        // public Vector4 maxClip;
        
        public int screenWidth;
        public int screenHeight;
        
        [ReadOnly] public NativeArray<Vector3> boundsPositionData;
        [ReadOnly] public NativeArray<Vector3> lossyScale;
        [ReadOnly] public NativeArray<Vector3> eulerAngles;
        [ReadOnly] public NativeArray<Vector3> position;

        public Vector3 cameraPos;
        public Vector3 cameraForward;
        public Vector3 cameraUp;
        public bool cameraOrthographic;
        public float cameraOrthographicSize;
        public float cameraFarClipPlane;
        public float cameraNearClipPlane;
        public float cameraFieldOfView;

        private Matrix4x4 _matModel;
        public Matrix4x4 _matView;
        public Matrix4x4 _matProjection;
        
        public NativeArray<bool> IsOccludee;

        const float MY_PI = 3.1415926f;
        const float D2R = MY_PI / 180.0f;
        public void Execute(int index)
        {
            if (IsOccludee[index] == false) return;
            _matModel = GetModelMatrix(index);
            Matrix4x4 mvpMat = _matProjection * _matView * _matModel;
            // Vector4[] clipAABB = URUtils.GetClipAABB(mesh.bounds, mvp);
            for (int i = 0; i < 12; i++)
            {
                Vector3Int triangle = boundstrianglesData[i];
                int idx0 = triangle.x + index * 24;
                int idx1 = triangle.y + index * 24;
                int idx2 = triangle.z + index * 24;
                
                var vert = boundsPositionData[idx0];
                var objVert = new Vector4(vert.x, vert.y, -vert.z, 1);
                var v0 = mvpMat * objVert;
            
                vert = boundsPositionData[idx1]; 
                objVert = new Vector4(vert.x, vert.y, -vert.z, 1);
                var v1 = mvpMat * objVert;
            
                vert = boundsPositionData[idx2]; 
                objVert = new Vector4(vert.x, vert.y, -vert.z, 1);
                var v2 = mvpMat * objVert;
                
                // ------ Clipping -------
                if (Clipped(v0, v1, v2))
                {
                    return;
                }

                // ------- Perspective division --------
                //clip space to NDC
                v0.x /= v0.w; v0.y /= v0.w; v0.z /= v0.w;
                v1.x /= v1.w; v1.y /= v1.w; v1.z /= v1.w;
                v2.x /= v2.w; v2.y /= v2.w; v2.z /= v2.w;
                
                //backface culling
                // {
                //     Vector3 t0 = new Vector3(v0.x, v0.y, v0.z);
                //     Vector3 t1 = new Vector3(v1.x, v1.y, v1.z);
                //     Vector3 t2 = new Vector3(v2.x, v2.y, v2.z);
                //     Vector3 e01 = t1 - t0;
                //     Vector3 e02 = t2 - t0;
                //     Vector3 cross = Vector3.Cross(e01, e02);
                //     if (cross.z > 0)
                //     {
                //         return;
                //     }
                // }

                // ------- Viewport Transform ----------
                //NDC to screen space            
                {
                    int max_w = screenWidth - 1;
                    int max_h = screenHeight - 1;
                    v0.x = 0.5f * max_w * (v0.x + 1.0f);
                    v0.y = 0.5f * max_h * (v0.y + 1.0f);                
                    v0.z = v0.z * 0.5f + 0.5f; 

                    v1.x = 0.5f * max_w * (v1.x + 1.0f);
                    v1.y = 0.5f * max_h * (v1.y + 1.0f);                
                    v1.z = v1.z * 0.5f + 0.5f; 

                    v2.x = 0.5f * max_w * (v2.x + 1.0f);
                    v2.y = 0.5f * max_h * (v2.y + 1.0f);                
                    v2.z = v2.z * 0.5f + 0.5f;
                }

                Triangle t = new Triangle();
                t.Vertex0.Position = v0;
                t.Vertex1.Position = v1;
                t.Vertex2.Position = v2;
                RasterizeTriangle(t,index);
            }
        }

        public int GetIndex(int x, int y)
        {
            return y * screenWidth + x;
        }
        void RasterizeTriangle(Triangle t,int obj_index)
        {                        
            var v0 = t.Vertex0.Position;
            var v1 = t.Vertex1.Position;
            var v2 = t.Vertex2.Position;            
            
            //Find out the bounding box of current triangle.
            float minX = (v0.x < v1.x) ? ((v0.x < v2.x)?v0.x : (v1.x < v2.x)?v1.x:v2.x) : ((v1.x < v2.x)?v1.x:(v0.x < v2.x)?v0.x:v2.x);
            float maxX = (v0.x > v1.x) ? ((v0.x > v2.x)?v0.x : (v1.x > v2.x)?v1.x:v2.x) : ((v1.x > v2.x)?v1.x:(v0.x > v2.x)?v0.x:v2.x);
            float minY = (v0.y < v1.y) ? ((v0.y < v2.y)?v0.y : (v1.y < v2.y)?v1.y:v2.y) : ((v1.y < v2.y)?v1.y:(v0.y < v2.y)?v0.y:v2.y);
            float maxY = (v0.y > v1.y) ? ((v0.y > v2.y)?v0.y : (v1.y > v2.y)?v1.y:v2.y) : ((v1.y > v2.y)?v1.y:(v0.y > v2.y)?v0.y:v2.y);
            
            float minZ = (v0.z < v1.z) ? ((v0.z < v2.z)?v0.x : (v1.z < v2.z)?v1.z:v2.z) : ((v1.z < v2.z)?v1.z:(v0.z < v2.z)?v0.z:v2.z);
            float maxZ = (v0.z > v1.z) ? ((v0.z > v2.z)?v0.z : (v1.z > v2.z)?v1.z:v2.z) : ((v1.z > v2.z)?v1.z:(v0.z > v2.z)?v0.z:v2.z);
                                        

            int minPX = Mathf.FloorToInt(minX);
            minPX = minPX < 0 ? 0 : minPX;
            int maxPX = Mathf.CeilToInt(maxX);
            maxPX = maxPX > screenWidth ? screenWidth : maxPX;
            int minPY = Mathf.FloorToInt(minY);
            minPY = minPY < 0 ? 0 : minPY;
            int maxPY = Mathf.CeilToInt(maxY);
            maxPY = maxPY > screenHeight ? screenHeight : maxPY;
            
            // int MidPX = Mathf.FloorToInt((maxX - minX)/2);
            // int MidPY = Mathf.FloorToInt((maxY - minY)/2);
            
            // int index = GetIndex(MidPX,MidPY);
            // frameBuffer[index] = Color.white;
            // if (minClip.z >= depthBuffer[index])
            //     NeedMoveToCullingLayer[0] = false;
            
            // index = GetIndex(Mathf.FloorToInt(v0.x),Mathf.FloorToInt(v0.y));
            // frameBuffer[index] = Color.white;
            // if (minClip.z >= depthBuffer[index])
            //     NeedMoveToCullingLayer[0] = false;
            //
            // index = GetIndex(Mathf.FloorToInt(v1.x),Mathf.FloorToInt(v1.y));
            // frameBuffer[index] = Color.white;
            // if (minClip.z >= depthBuffer[index])
            //     NeedMoveToCullingLayer[0] = false;
            //
            // index = GetIndex(Mathf.FloorToInt(v2.x),Mathf.FloorToInt(v2.y));
            // frameBuffer[index] = Color.white;
            // if (minClip.z >= depthBuffer[index])
            //     NeedMoveToCullingLayer[0] = false;
            // 遍历当前三角形包围中的所有像素，判断当前像素是否在三角形中
            // 对于在三角形中的像素，使用重心坐标插值得到深度值，并使用z buffer进行深度测试和写入
            for (int y = minPY; y < maxPY; ++y)
            {
                for(int x = minPX; x < maxPX; ++x)
                {
                    //深度测试(注意我们这儿的z值越大越靠近near plane，因此大值通过测试）
                    int index = GetIndex(x, y);
                    frameBuffer[index] = Color.yellow;
                    if (maxZ >= depthBuffer[index])
                    {
                        frameBuffer[index] = Color.blue;
                        NeedMoveToCullingLayer[obj_index] = false;
                    }
                }
            }
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
        public Matrix4x4 GetModelMatrix(int index)
        {
            var matScale = TransformTool.GetScaleMatrix(lossyScale[index]);

            var rotation = eulerAngles[index];
            var rotX = TransformTool.GetRotationMatrix(Vector3.right, -rotation.x);
            var rotY = TransformTool.GetRotationMatrix(Vector3.up, -rotation.y);
            var rotZ = TransformTool.GetRotationMatrix(Vector3.forward, rotation.z);
            var matRot = rotY * rotX * rotZ; // rotation apply order: z(roll), x(pitch), y(yaw) 

            var matTranslation = TransformTool.GetTranslationMatrix(position[index]);

            return matTranslation * matRot * matScale;
        }
        
        public void SetupViewProjectionMatrix(Vector3 cameraPos,Vector3 cameraForward,Vector3 cameraUp,
            out Matrix4x4 ViewMatrix, out Matrix4x4 ProjectionMatrix)
        {
            float aspect = (float)screenWidth / screenHeight;
            //左手坐标系转右手坐标系,以下坐标和向量z取反
            var camPos = cameraPos;
            camPos.z *= -1; 
            var lookAt = cameraForward;
            lookAt.z *= -1;
            var up = cameraUp;
            up.z *= -1;
            
            ViewMatrix = TransformTool.GetViewMatrix(camPos, lookAt, up);
        
            if (cameraOrthographic)
            {
                float halfOrthHeight = cameraOrthographicSize;
                float halfOrthWidth = halfOrthHeight * aspect;
                float f = -cameraFarClipPlane;
                float n = -cameraNearClipPlane;
                ProjectionMatrix = GetOrthographicProjectionMatrix(-halfOrthWidth, halfOrthWidth, -halfOrthHeight, halfOrthHeight, f, n);
            }
            else
            {
                ProjectionMatrix = GetPerspectiveProjectionMatrix(cameraFieldOfView, aspect, cameraNearClipPlane, cameraFarClipPlane);
            }
        }
        
        public static Matrix4x4 GetPerspectiveProjectionMatrix(float l, float r, float b, float t, float f, float n)
        {
            Matrix4x4 perspToOrtho = Matrix4x4.identity;
            perspToOrtho.m00 = n;
            perspToOrtho.m11 = n;
            perspToOrtho.m22 = n + f;
            perspToOrtho.m23 = -n * f;
            perspToOrtho.m32 = 1;
            perspToOrtho.m33 = 0;
            var orthoProj = GetOrthographicProjectionMatrix(l, r, b, t, f, n);
            return orthoProj * perspToOrtho;
        }
        
        public static Matrix4x4 GetPerspectiveProjectionMatrix(float eye_fov, float aspect_ratio, float zNear, float zFar)
        {
            float t = zNear * Mathf.Tan(eye_fov * D2R * 0.5f);
            float b = -t;
            float r = t * aspect_ratio;
            float l = -r;
            float n = -zNear;
            float f = -zFar;
            return GetPerspectiveProjectionMatrix(l, r, b, t, f, n);
        }
        
        public static Matrix4x4 GetOrthographicProjectionMatrix(float l, float r, float b, float t, float f, float n)
        {
            Matrix4x4 translate = Matrix4x4.identity;
            translate.SetColumn(3, new Vector4(-(r + l) * 0.5f, -(t + b) * 0.5f, -(n + f) * 0.5f, 1f));
            Matrix4x4 scale = Matrix4x4.identity;
            scale.m00 = 2f / (r - l);
            scale.m11 = 2f / (t - b);
            scale.m22 = 2f / (n - f);
            return scale * translate;
        }
    }
}

