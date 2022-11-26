using System;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using UnityEditor.Playables;
using UnityEngine;
using UnityEngine.Profiling;

namespace SoftOcclusionCulling
{
    public class CPURasterizer : IRasterizer
    {
        private int _width, _height;
     
        private SoftOcclusionCullingFeature.PassSettings passSettings;
        
        Matrix4x4 _matModel;
        Matrix4x4 _matView;
        Matrix4x4 _matProjection;        

        Color[] frame_buf;
        float[] depth_buf;
        Color[] temp_buf;

        Color[] samplers_color_MSAA;
        bool[] samplers_mask_MSAA;
        float[] samplers_depth_MSAA;
        
        public Texture2D texture;
        
        ShaderUniforms Uniforms;  
        
        public String Name { get=>"CPU"; }
        public Texture ColorTexture { get=>texture; }
        
        //Stats
        int _trianglesAll, _trianglesRendered;
        int _verticesAll;

        public OnRasterizerStatUpdate StatDelegate;

        //优化GC
        Vector4[] _tmpVector4s = new Vector4[3];        
        Vector3[] _tmpVector3s = new Vector3[3];
        
        // Object最大最小深度
        private Vector4 minClip;
        private Vector4 maxClip;
        
        public float Aspect {
            get { return (float)_width / _height; }
        }

        public CPURasterizer(int w, int h,SoftOcclusionCullingFeature.PassSettings passSettings)
        {
            this.passSettings = passSettings;
            
            _width = w;
            _height = h;

            frame_buf = new Color[w * h];
            depth_buf = new float[w * h];
            temp_buf = new Color[w * h];

            texture = new Texture2D(w, h);
            texture.filterMode = FilterMode.Point;
            
        }
        
        public void Clear(BufferMask mask)
        {
            Profiler.BeginSample("CPURasterizer.Clear");
            if ((mask & BufferMask.Color) == BufferMask.Color) {
                URUtils.FillArray(frame_buf,Color.black);
            }

            if ((mask & BufferMask.Depth) == BufferMask.Depth) {
                URUtils.FillArray(depth_buf,0f);
            }
            
            _trianglesAll = _trianglesRendered = 0;
            _verticesAll = 0;
            
            Profiler.EndSample();
        }

        public void SetupUniforms(Camera camera, Light mainLight)
        {
            var camPos = camera.transform.position;
            camPos.z *= -1;
            Uniforms.WorldSpaceCameraPos = camPos;

            var lightDir = mainLight.transform.forward;
            lightDir.z *= -1;
            Uniforms.WorldSpaceLightDir = -lightDir;
            Uniforms.LightColor = mainLight.color * mainLight.intensity;
            
            TransformTool.SetupViewProjectionMatrix(camera, Aspect, out _matView, out _matProjection);

        }

        public void DrawObject(RenderingObject ro)
        {
            Profiler.BeginSample("CPURasterizer.DrawObject");
            if (ro.mesh == null || ro.cpuData == null) {
                Profiler.EndSample();
                return;
            }
            Mesh mesh = ro.mesh;
            _matModel = ro.GetModelMatrix();
            
            Matrix4x4 mvp = _matProjection * _matView * _matModel;
            // 视锥体剔除
            if (this.passSettings.FrustumCulling && URUtils.FrustumCulling(mesh.bounds, mvp)) {
                Profiler.EndSample();
                return;
            }
            
            Vector4[] clipAABB = URUtils.GetClipAABB(mesh.bounds, mvp);
            minClip = clipAABB[0];
            maxClip = clipAABB[7];
            
            minClip.z /= minClip.w;
            maxClip.z /= maxClip.w;
            
            minClip.z = minClip.z * 0.5f + 0.5f;
            maxClip.z = maxClip.z * 0.5f + 0.5f;
            
            Matrix4x4 normalMat = _matModel.inverse.transpose;

            _verticesAll += mesh.vertexCount;
            _trianglesAll += ro.cpuData.MeshTriangles.Length / 3;
            
            //Unity模型本地坐标系也是左手系，需要转成我们使用的右手系
            //1. z轴反转
            //2. 三角形顶点环绕方向从顺时针改成逆时针
            /// ------------- Vertex Shader -------------------
            VSOutBuf[] vsOutput = ro.cpuData.vsOutputBuffer;
            
            Profiler.BeginSample("CPURasterizer.VertexShader CPU");
            for (int i = 0; i < mesh.vertexCount; i++)
            {
                var vert = ro.cpuData.MeshVertices[i];
                var objVert = new Vector4(vert.x, vert.y, -vert.z, 1);
                vsOutput[i].clipPos = mvp * objVert;
                vsOutput[i].worldPos = _matModel * objVert;
                var normal = ro.cpuData.MeshNormals[i];
                var objNormal = new Vector3(normal.x, normal.y, -normal.z);
                vsOutput[i].objectNormal = objNormal;
                vsOutput[i].worldNormal = normalMat * objNormal;
            }
            Profiler.EndSample();
            
            Profiler.BeginSample("CPURasterizer.PrimitiveAssembly");
            
            var indices = ro.cpuData.MeshTriangles;
            for(int i=0; i< indices.Length; i+=3)
            {         
                /// -------------- Primitive Assembly -----------------
            
                //注意这儿对调了v0和v1的索引，因为原来的 0,1,2是顺时针的，对调后是 1,0,2是逆时针的
                //Unity Quard模型的两个三角形索引分别是 0,3,1,3,0,2 转换后为 3,0,1,0,3,2
                int idx0 = indices[i+1];
                int idx1 = indices[i]; 
                int idx2 = indices[i+2];  
            
                var v = _tmpVector4s;                                           
                
                v[0] = vsOutput[idx0].clipPos;
                v[1] = vsOutput[idx1].clipPos;
                v[2] = vsOutput[idx2].clipPos;                                  
                
                // ------ Clipping -------
                if (Clipped(_tmpVector4s)) {
                    continue;
                }                
            
                // ------- Perspective division --------
                //clip space to NDC
                for (int k=0; k<3; k++)
                {
                    v[k].x /= v[k].w;
                    v[k].y /= v[k].w;
                    v[k].z /= v[k].w;
                }
            
                //backface culling
                if (passSettings.BackFaceCulling)
                {
                    Vector3 v0 = new Vector3(v[0].x, v[0].y, v[0].z);
                    Vector3 v1 = new Vector3(v[1].x, v[1].y, v[1].z);
                    Vector3 v2 = new Vector3(v[2].x, v[2].y, v[2].z);
                    Vector3 e01 = v1 - v0;
                    Vector3 e02 = v2 - v0;
                    Vector3 cross = Vector3.Cross(e01, e02);
                    if (cross.z < 0) {
                        continue;
                    }
                }
            
                ++_trianglesRendered;
            
                // ------- Viewport Transform ----------
                //NDC to screen space
                for (int k = 0; k < 3; k++)
                {
                    var vec = v[k];
                    vec.x = 0.5f * (_width - 1) * (vec.x + 1.0f);
                    vec.y = 0.5f * (_height -1) * (vec.y + 1.0f);
            
                    //在硬件渲染中，NDC的z值经过硬件的透视除法之后就直接写入到depth buffer了，如果要调整需要在投影矩阵中调整
                    //由于我们是软件渲染，所以可以在这里调整z值。                    
            
                    //GAMES101约定的NDC是右手坐标系，z值范围是[-1,1]，但n为1，f为-1，因此值越大越靠近n。                    
                    //为了可视化Depth buffer，将最终的z值从[-1,1]映射到[0,1]的范围，因此最终n为1, f为0。离n越近，深度值越大。                    
                    //由于远处的z值为0，因此clear时深度要清除为0，然后深度测试时，使用GREATER测试。
                    //(当然我们也可以在这儿反转z值，然后clear时使用float.MaxValue清除，并且深度测试时使用LESS_EQUAL测试)
                    //注意：这儿的z值调整并不是必要的，只是为了可视化时便于映射为颜色值。其实也可以在可视化的地方调整。
                    //但是这么调整后，正好和Unity在DirectX平台的Reverse z一样，让near plane附近的z值的浮点数精度提高。
                    vec.z = vec.z * 0.5f + 0.5f;

                    v[k] = vec; 
                }
            
                Triangle t = new Triangle();
                t.Vertex0.Position = v[0];
                t.Vertex1.Position = v[1];
                t.Vertex2.Position = v[2];                
            
                //set obj normal
                t.Vertex0.Normal = vsOutput[idx0].objectNormal;
                t.Vertex1.Normal = vsOutput[idx1].objectNormal;
                t.Vertex2.Normal = vsOutput[idx2].objectNormal;                
            
                if (ro.cpuData.MeshUVs.Length > 0)
                {                    
                    t.Vertex0.Texcoord = ro.cpuData.MeshUVs[idx0];
                    t.Vertex1.Texcoord = ro.cpuData.MeshUVs[idx1];
                    t.Vertex2.Texcoord = ro.cpuData.MeshUVs[idx2];                    
                }
            
                //设置顶点色,使用config中的颜色数组循环设置                
                t.Vertex0.Color = Color.white;
                t.Vertex1.Color = Color.white;
                t.Vertex2.Color = Color.white;
            
                //set world space pos & normal
                t.Vertex0.WorldPos = vsOutput[idx0].worldPos;
                t.Vertex1.WorldPos = vsOutput[idx1].worldPos;
                t.Vertex2.WorldPos = vsOutput[idx2].worldPos;
                t.Vertex0.WorldNormal = vsOutput[idx0].worldNormal;
                t.Vertex1.WorldNormal = vsOutput[idx1].worldNormal;
                t.Vertex2.WorldNormal = vsOutput[idx2].worldNormal;
            
                /// ---------- Rasterization -----------
                RasterizeTriangle(t, ro);
                
            }
            
            Profiler.EndSample();
            
            Profiler.EndSample();
            
            // URUtils.FillArray(frame_buf,Color.blue);
        }
        
        public void OcclusionCulling(RenderingObject ro)
        {
            Profiler.BeginSample("CPURasterizer.DrawObject");
            if (ro.mesh == null || ro.cpuData == null) {
                Profiler.EndSample();
                return;
            }
            Mesh mesh = ro.mesh;
            _matModel = ro.GetModelMatrix();
            
            Matrix4x4 mvp = _matProjection * _matView * _matModel;
            // 视锥体剔除
            if (this.passSettings.FrustumCulling && URUtils.FrustumCulling(mesh.bounds, mvp)) {
                Profiler.EndSample();
                return;
            }
            
            Vector4[] clipAABB = URUtils.GetClipAABB(mesh.bounds, mvp);
            minClip = clipAABB[0];
            maxClip = clipAABB[7];
            
            minClip.z /= minClip.w;
            maxClip.z /= maxClip.w;
            
            minClip.z = minClip.z * 0.5f + 0.5f;
            maxClip.z = maxClip.z * 0.5f + 0.5f;
            
            Matrix4x4 normalMat = _matModel.inverse.transpose;

            _verticesAll += mesh.vertexCount;
            _trianglesAll += ro.cpuData.MeshTriangles.Length / 3;
            
            //Unity模型本地坐标系也是左手系，需要转成我们使用的右手系
            //1. z轴反转
            //2. 三角形顶点环绕方向从顺时针改成逆时针
            /// ------------- Vertex Shader -------------------
            VSOutBuf[] vsOutput = ro.cpuData.vsOutputBuffer;
            
            Profiler.BeginSample("CPURasterizer.VertexShader CPU");
            for (int i = 0; i < mesh.vertexCount; i++)
            {
                var vert = ro.cpuData.MeshVertices[i];
                var objVert = new Vector4(vert.x, vert.y, -vert.z, 1);
                vsOutput[i].clipPos = mvp * objVert;
                vsOutput[i].worldPos = _matModel * objVert;
                var normal = ro.cpuData.MeshNormals[i];
                var objNormal = new Vector3(normal.x, normal.y, -normal.z);
                vsOutput[i].objectNormal = objNormal;
                vsOutput[i].worldNormal = normalMat * objNormal;
            }
            Profiler.EndSample();
            
            Profiler.BeginSample("CPURasterizer.PrimitiveAssembly");
            
            var indices = ro.cpuData.MeshTriangles;
            for(int i=0; i< indices.Length; i+=3)
            {         
                /// -------------- Primitive Assembly -----------------
            
                //注意这儿对调了v0和v1的索引，因为原来的 0,1,2是顺时针的，对调后是 1,0,2是逆时针的
                //Unity Quard模型的两个三角形索引分别是 0,3,1,3,0,2 转换后为 3,0,1,0,3,2
                int idx0 = indices[i+1];
                int idx1 = indices[i]; 
                int idx2 = indices[i+2];  
            
                var v = _tmpVector4s;                                           
                
                v[0] = vsOutput[idx0].clipPos;
                v[1] = vsOutput[idx1].clipPos;
                v[2] = vsOutput[idx2].clipPos;                                  
                
                // ------ Clipping -------
                if (Clipped(_tmpVector4s)) {
                    continue;
                }                
            
                // ------- Perspective division --------
                //clip space to NDC
                for (int k=0; k<3; k++)
                {
                    v[k].x /= v[k].w;
                    v[k].y /= v[k].w;
                    v[k].z /= v[k].w;
                }
            
                //backface culling
                if (passSettings.BackFaceCulling)
                {
                    Vector3 v0 = new Vector3(v[0].x, v[0].y, v[0].z);
                    Vector3 v1 = new Vector3(v[1].x, v[1].y, v[1].z);
                    Vector3 v2 = new Vector3(v[2].x, v[2].y, v[2].z);
                    Vector3 e01 = v1 - v0;
                    Vector3 e02 = v2 - v0;
                    Vector3 cross = Vector3.Cross(e01, e02);
                    if (cross.z < 0) {
                        continue;
                    }
                }
            
                ++_trianglesRendered;
            
                // ------- Viewport Transform ----------
                //NDC to screen space
                for (int k = 0; k < 3; k++)
                {
                    var vec = v[k];
                    vec.x = 0.5f * (_width - 1) * (vec.x + 1.0f);
                    vec.y = 0.5f * (_height -1) * (vec.y + 1.0f);
            
                    //在硬件渲染中，NDC的z值经过硬件的透视除法之后就直接写入到depth buffer了，如果要调整需要在投影矩阵中调整
                    //由于我们是软件渲染，所以可以在这里调整z值。                    
            
                    //GAMES101约定的NDC是右手坐标系，z值范围是[-1,1]，但n为1，f为-1，因此值越大越靠近n。                    
                    //为了可视化Depth buffer，将最终的z值从[-1,1]映射到[0,1]的范围，因此最终n为1, f为0。离n越近，深度值越大。                    
                    //由于远处的z值为0，因此clear时深度要清除为0，然后深度测试时，使用GREATER测试。
                    //(当然我们也可以在这儿反转z值，然后clear时使用float.MaxValue清除，并且深度测试时使用LESS_EQUAL测试)
                    //注意：这儿的z值调整并不是必要的，只是为了可视化时便于映射为颜色值。其实也可以在可视化的地方调整。
                    //但是这么调整后，正好和Unity在DirectX平台的Reverse z一样，让near plane附近的z值的浮点数精度提高。
                    vec.z = vec.z * 0.5f + 0.5f;

                    v[k] = vec; 
                }
            
                Triangle t = new Triangle();
                t.Vertex0.Position = v[0];
                t.Vertex1.Position = v[1];
                t.Vertex2.Position = v[2];                
            
                //set obj normal
                t.Vertex0.Normal = vsOutput[idx0].objectNormal;
                t.Vertex1.Normal = vsOutput[idx1].objectNormal;
                t.Vertex2.Normal = vsOutput[idx2].objectNormal;                
            
                if (ro.cpuData.MeshUVs.Length > 0)
                {                    
                    t.Vertex0.Texcoord = ro.cpuData.MeshUVs[idx0];
                    t.Vertex1.Texcoord = ro.cpuData.MeshUVs[idx1];
                    t.Vertex2.Texcoord = ro.cpuData.MeshUVs[idx2];                    
                }
            
                //设置顶点色,使用config中的颜色数组循环设置                
                t.Vertex0.Color = Color.white;
                t.Vertex1.Color = Color.white;
                t.Vertex2.Color = Color.white;
            
                //set world space pos & normal
                t.Vertex0.WorldPos = vsOutput[idx0].worldPos;
                t.Vertex1.WorldPos = vsOutput[idx1].worldPos;
                t.Vertex2.WorldPos = vsOutput[idx2].worldPos;
                t.Vertex0.WorldNormal = vsOutput[idx0].worldNormal;
                t.Vertex1.WorldNormal = vsOutput[idx1].worldNormal;
                t.Vertex2.WorldNormal = vsOutput[idx2].worldNormal;
            
                /// ---------- Rasterization -----------
                RasterizeTriangle(t, ro);
                
            }
            
            Profiler.EndSample();
            
            Profiler.EndSample();
            
            // URUtils.FillArray(frame_buf,Color.blue);
        }


        public void UpdateFrame()
        {
            Profiler.BeginSample("CPURasterizer.UpdateFrame");
            switch (passSettings.DisplayBuffer)
            {
                case DisplayBufferType.Color:
                    texture.SetPixels(frame_buf);
                    break;
                case DisplayBufferType.DepthRed:
                case DisplayBufferType.DepthGray:
                    for (int i = 0; i < depth_buf.Length; ++i)
                    {
                        //depth_buf中的值范围是[0,1]，且最近处为1，最远处为0。因此可视化后背景是黑色
                        float c = depth_buf[i]; 
                        if(passSettings.DisplayBuffer == DisplayBufferType.DepthRed)
                        {
                            temp_buf[i] = new Color(c, 0, 0);
                        }
                        else
                        {
                            temp_buf[i] = new Color(c, c, c);
                        }                        
                    }
                    texture.SetPixels(temp_buf);
                    break;
            }
            texture.Apply();
            
            if (StatDelegate != null) {
                StatDelegate(_verticesAll, _trianglesAll, _trianglesRendered);
            }
            
            Profiler.EndSample();
        }

        public void Release()
        {
            texture = null;
            frame_buf = null;
            depth_buf = null;
            temp_buf = null;
            samplers_color_MSAA = null;            
            samplers_mask_MSAA = null; 
            samplers_depth_MSAA = null;  
        }
        
        //三角形Clipping操作，对于部分在clipping volume中的图元，
        //硬件实现时一般只对部分顶点z值在near,far之间的图元进行clipping操作，
        //而部分顶点x,y值在x,y裁剪平面之间的图元则不进行裁剪，只是通过一个比viewport更大一些的guard-band区域进行整体剔除（相当于放大x,y的测试范围）
        //这样x,y裁剪平面之间的图元最终在frame buffer上进行Scissor测试。
        //此处的实现简化为只整体的视锥剔除，不做任何clipping操作。对于x,y裁剪没问题，虽然没扩大region,也可以最后在frame buffer上裁剪掉。
        //对于z的裁剪由于没有处理，会看到整个三角形消失导致的边缘不齐整

        //直接使用Clip space下的视锥剔除算法   
        [MethodImpl(MethodImplOptions.AggressiveInlining)]                 
        bool Clipped(Vector4[] v)
        {            
            //分别检查视锥体的六个面，如果三角形所有三个顶点都在某个面之外，则该三角形在视锥外，剔除  
            //由于NDC中总是满足-1<=Zndc<=1, 而当 w < 0 时，-w >= Zclip = Zndc*w >= w。所以此时clip space的坐标范围是[w,-w], 为了比较时更明确，将w取正      
            var v0 = v[0];
            var w0 = v0.w >=0 ? v0.w : -v0.w;
            var v1 = v[1];
            var w1 = v1.w >=0 ? v1.w : -v1.w;
            var v2 = v[2];
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
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        Vector3 ComputeBarycentric2D(float x, float y, Triangle t)
        {
            Profiler.BeginSample("CPURasterizer.ComputeBarycentric2D");
            var v = _tmpVector4s;            
            v[0] = t.Vertex0.Position;
            v[1] = t.Vertex1.Position;
            v[2] = t.Vertex2.Position;
            
            float c1 = (x * (v[1].y - v[2].y) + (v[2].x - v[1].x) * y + v[1].x * v[2].y - v[2].x * v[1].y) / (v[0].x * (v[1].y - v[2].y) + (v[2].x - v[1].x) * v[0].y + v[1].x * v[2].y - v[2].x * v[1].y);
            float c2 = (x * (v[2].y - v[0].y) + (v[0].x - v[2].x) * y + v[2].x * v[0].y - v[0].x * v[2].y) / (v[1].x * (v[2].y - v[0].y) + (v[0].x - v[2].x) * v[1].y + v[2].x * v[0].y - v[0].x * v[2].y);
            float c3 = (x * (v[0].y - v[1].y) + (v[1].x - v[0].x) * y + v[0].x * v[1].y - v[1].x * v[0].y) / (v[2].x * (v[0].y - v[1].y) + (v[1].x - v[0].x) * v[2].y + v[0].x * v[1].y - v[1].x * v[0].y);
            
            Profiler.EndSample();
            return new Vector3(c1, c2, c3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetIndex(int x, int y)
        {
            return y * _width + x;
        }
        
        //Screen space  rasterization
        void RasterizeTriangle(Triangle t, RenderingObject ro)
        {
            Profiler.BeginSample("CPURasterizer.RasterizeTriangle");
            var v = _tmpVector4s;
            v[0] = t.Vertex0.Position;
            v[1] = t.Vertex1.Position;
            v[2] = t.Vertex2.Position;            
            
            //Find out the bounding box of current triangle.
            float minX = v[0].x;
            float maxX = minX;
            float minY = v[0].y;
            float maxY = minY;

            for(int i=1; i<3; ++i)
            {
                float x = v[i].x;
                if(x < minX)
                {
                    minX = x;
                } else if(x > maxX)
                {
                    maxX = x;
                }
                float y = v[i].y;
                if(y < minY)
                {
                    minY = y;
                }else if(y > maxY)
                {
                    maxY = y;
                }
            }

            int minPX = Mathf.FloorToInt(minX);
            minPX = minPX < 0 ? 0 : minPX;
            int maxPX = Mathf.CeilToInt(maxX);
            maxPX = maxPX > _width ? _width : maxPX;
            int minPY = Mathf.FloorToInt(minY);
            minPY = minPY < 0 ? 0 : minPY;
            int maxPY = Mathf.CeilToInt(maxY);
            maxPY = maxPY > _height ? _height : maxPY;


            // 遍历当前三角形包围中的所有像素，判断当前像素是否在三角形中
            // 对于在三角形中的像素，使用重心坐标插值得到深度值，并使用z buffer进行深度测试和写入
            for(int y = minPY; y < maxPY; ++y)
            {
                for(int x = minPX; x < maxPX; ++x)
                {
                    //if(IsInsideTriangle(x, y, t)) //-->检测是否在三角形内比使用重心坐标检测要慢，因此先计算重心坐标，再检查3个坐标是否有小于0
                    {
                        int index = GetIndex(x, y);
                        frame_buf[index] = Color.white;
                        if (minClip.z >= depth_buf[index])
                        {
                            depth_buf[index] = minClip.z;
                            ro.NeedMoveToCullingLayer = false;
                        }
                        

                        // //计算重心坐标
                        // var c = ComputeBarycentric2D(x, y, t);
                        // float alpha = c.x;
                        // float beta = c.y;
                        // float gamma = c.z;
                        // if(alpha < 0 || beta < 0 || gamma < 0){                                
                        //     continue;
                        // }
                        // //透视校正插值，z为透视校正插值后的view space z值
                        // float z = 1.0f / (alpha / v[0].w + beta / v[1].w + gamma / v[2].w);
                        // //zp为透视校正插值后的screen space z值
                        // float zp = (alpha * v[0].z / v[0].w + beta * v[1].z / v[1].w + gamma * v[2].z / v[2].w) * z;
                        //
                        // //深度测试(注意我们这儿的z值越大越靠近near plane，因此大值通过测试）
                        // // int index = GetIndex(x, y);
                        // if(zp >= depth_buf[index])
                        // {
                        //     ro.NeedMoveToCullingLayer = false;
                        //     depth_buf[index] = zp;
                        //
                        //     // if (passSettings.FragmentShaderType == ShaderType.OnlyDepth) 
                        //     //     continue;
                        //     
                        //     //透视校正插值
                        //     Profiler.BeginSample("CPURasterizer.AttributeInterpolation");
                        //     Color color_p = (alpha * t.Vertex0.Color / v[0].w + beta * t.Vertex1.Color / v[1].w + gamma * t.Vertex2.Color / v[2].w) * z;
                        //     Vector2 uv_p = (alpha * t.Vertex0.Texcoord / v[0].w + beta * t.Vertex1.Texcoord / v[1].w + gamma * t.Vertex2.Texcoord / v[2].w) * z;
                        //     Vector3 normal_p = (alpha * t.Vertex0.Normal / v[0].w + beta * t.Vertex1.Normal  / v[1].w + gamma * t.Vertex2.Normal  / v[2].w) * z;
                        //     Vector3 worldPos_p = (alpha * t.Vertex0.WorldPos / v[0].w + beta * t.Vertex1.WorldPos / v[1].w + gamma * t.Vertex2.WorldPos / v[2].w) * z;
                        //     Vector3 worldNormal_p = (alpha * t.Vertex0.WorldNormal / v[0].w + beta * t.Vertex1.WorldNormal / v[1].w + gamma * t.Vertex2.WorldNormal / v[2].w) * z;
                        //     Profiler.EndSample();
                        //     
                        //     FragmentShaderInputData input = new FragmentShaderInputData();
                        //     input.Color = color_p;
                        //     input.UV = uv_p;
                        //     input.TextureData = ro.texture.GetPixelData<URColor24>(0);
                        //     input.TextureWidth = ro.texture.width;
                        //     input.TextureHeight = ro.texture.height;
                        //     input.UseBilinear = passSettings.BilinearSample;
                        //     input.LocalNormal = normal_p;
                        //     input.WorldPos = worldPos_p;
                        //     input.WorldNormal = worldNormal_p;
                        //
                        //     Profiler.BeginSample("CPURasterizer.FragmentShader");
                        //     switch(passSettings.FragmentShaderType){
                        //         case ShaderType.BlinnPhong:
                        //             frame_buf[index] = ShaderContext.FSBlinnPhong(input, Uniforms);
                        //             break;
                        //         case ShaderType.NormalVisual:
                        //             frame_buf[index] = ShaderContext.FSNormalVisual(input);
                        //             break;
                        //         case ShaderType.VertexColor:
                        //             frame_buf[index] = ShaderContext.FSVertexColor(input);
                        //             break;
                        //     }
                        //     
                        //     Profiler.EndSample();                                                                                                
                        // }
                    }                        
                }
            }

            Profiler.EndSample();
        }
    }
}

