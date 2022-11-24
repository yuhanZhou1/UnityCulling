using System;
using UnityEngine;
using System.Runtime.CompilerServices;

namespace URasterizer
{    
    public class CPURasterizer : IRasterizer
    {
        int _width;
        int _height;

        RenderingConfig _config;

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

        //Stats
        int _trianglesAll, _trianglesRendered;
        int _verticesAll;

        public OnRasterizerStatUpdate StatDelegate;

        //�Ż�GC
        Vector4[] _tmpVector4s = new Vector4[3];        
        Vector3[] _tmpVector3s = new Vector3[3];

        public String Name { get=>"CPU"; }

        public Texture ColorTexture { get=>texture; }


        public CPURasterizer(int w, int h, RenderingConfig config)
        {
            Debug.Log($"CPU Rasterizer screen size: {w}x{h}");

            _config = config;

            _width = w;
            _height = h;

            frame_buf = new Color[w * h];
            depth_buf = new float[w * h];
            temp_buf = new Color[w * h];

            texture = new Texture2D(w, h);
            texture.filterMode = FilterMode.Point;

            if (_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
            {
                AllocateMSAABuffers();
            }
        }

        void AllocateMSAABuffers()
        {
            int MSAALevel = (int)_config.MSAA;
            int bufSize = _width * _height * MSAALevel * MSAALevel;
            if(samplers_color_MSAA==null || samplers_color_MSAA.Length != bufSize)
            {
                samplers_color_MSAA = new Color[bufSize];
                samplers_mask_MSAA = new bool[bufSize];
                samplers_depth_MSAA = new float[bufSize];
            }            
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

        public float Aspect
        {
            get
            {
                return (float)_width / _height;
            }
        }

        

        public void Clear(BufferMask mask)
        {
            ProfileManager.BeginSample("CPURasterizer.Clear");

            if (_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
            {
                AllocateMSAABuffers();
            }

            if ((mask & BufferMask.Color) == BufferMask.Color)
            {                
                URUtils.FillArray(frame_buf, _config.ClearColor);
                if (_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
                {
                    URUtils.FillArray(samplers_color_MSAA, _config.ClearColor);
                    URUtils.FillArray(samplers_mask_MSAA, false);
                }
            }
            if((mask & BufferMask.Depth) == BufferMask.Depth)
            {
                URUtils.FillArray(depth_buf, 0f);
                if (_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
                {
                    URUtils.FillArray(samplers_depth_MSAA, 0f);
                }
            }

            _trianglesAll = _trianglesRendered = 0;
            _verticesAll = 0;

            ProfileManager.EndSample();
            
        }        

        public void SetupUniforms(Camera camera, Light mainLight)
        {            
            ShaderContext.Config = _config;

            var camPos = camera.transform.position;
            camPos.z *= -1;
            Uniforms.WorldSpaceCameraPos = camPos;            

            var lightDir = mainLight.transform.forward;
            lightDir.z *= -1;
            Uniforms.WorldSpaceLightDir = -lightDir;
            Uniforms.LightColor = mainLight.color * mainLight.intensity;
            Uniforms.AmbientColor = _config.AmbientColor;
            
            TransformTool.SetupViewProjectionMatrix(camera, Aspect, out _matView, out _matProjection);
        }

        

        public void DrawObject(RenderingObject ro)
        {
            ProfileManager.BeginSample("CPURasterizer.DrawObject");

            Mesh mesh = ro.mesh;
            
            _matModel = ro.GetModelMatrix();                       

            Matrix4x4 mvp = _matProjection * _matView * _matModel;
            if(_config.FrustumCulling && URUtils.FrustumCulling(mesh.bounds, mvp)){                
                ProfileManager.EndSample();
                return;
            }

            Matrix4x4 normalMat = _matModel.inverse.transpose;

            _verticesAll += mesh.vertexCount;
            _trianglesAll += ro.cpuData.MeshTriangles.Length / 3;
            

            //Unityģ�ͱ�������ϵҲ������ϵ����Ҫת������ʹ�õ�����ϵ
            //1. z�ᷴת
            //2. �����ζ��㻷�Ʒ����˳ʱ��ĳ���ʱ��


            /// ------------- Vertex Shader -------------------
            VSOutBuf[] vsOutput = ro.cpuData.vsOutputBuffer;                   
            
            ProfileManager.BeginSample("CPURasterizer.VertexShader CPU");
            for(int i=0; i<mesh.vertexCount; ++i)
            {                
                var vert = ro.cpuData.MeshVertices[i];        
                var objVert = new Vector4(vert.x, vert.y, -vert.z, 1); //ע�������ת��z����
                vsOutput[i].clipPos = mvp * objVert;
                vsOutput[i].worldPos = _matModel * objVert;
                var normal = ro.cpuData.MeshNormals[i];
                var objNormal = new Vector3(normal.x, normal.y, -normal.z);
                vsOutput[i].objectNormal = objNormal;
                vsOutput[i].worldNormal = normalMat * objNormal;
            }
            ProfileManager.EndSample();            
            
            ProfileManager.BeginSample("CPURasterizer.PrimitiveAssembly");

            var indices = ro.cpuData.MeshTriangles;
            for(int i=0; i< indices.Length; i+=3)
            {         
                /// -------------- Primitive Assembly -----------------

                //ע������Ե���v0��v1����������Ϊԭ���� 0,1,2��˳ʱ��ģ��Ե����� 1,0,2����ʱ���
                //Unity Quardģ�͵����������������ֱ��� 0,3,1,3,0,2 ת����Ϊ 3,0,1,0,3,2
                int idx0 = indices[i+1];
                int idx1 = indices[i]; 
                int idx2 = indices[i+2];  

                var v = _tmpVector4s;                                           
                
                v[0] = vsOutput[idx0].clipPos;
                v[1] = vsOutput[idx1].clipPos;
                v[2] = vsOutput[idx2].clipPos;                                  
                
                // ------ Clipping -------
                if (Clipped(_tmpVector4s))
                {
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
                if (_config.BackfaceCulling && !ro.DoubleSideRendering)
                {
                    Vector3 v0 = new Vector3(v[0].x, v[0].y, v[0].z);
                    Vector3 v1 = new Vector3(v[1].x, v[1].y, v[1].z);
                    Vector3 v2 = new Vector3(v[2].x, v[2].y, v[2].z);
                    Vector3 e01 = v1 - v0;
                    Vector3 e02 = v2 - v0;
                    Vector3 cross = Vector3.Cross(e01, e02);
                    if (cross.z < 0)
                    {
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

                    //��Ӳ����Ⱦ�У�NDC��zֵ����Ӳ����͸�ӳ���֮���ֱ��д�뵽depth buffer�ˣ����Ҫ������Ҫ��ͶӰ�����е���
                    //���������������Ⱦ�����Կ������������zֵ��                    

                    //GAMES101Լ����NDC����������ϵ��zֵ��Χ��[-1,1]����nΪ1��fΪ-1�����ֵԽ��Խ����n��                    
                    //Ϊ�˿��ӻ�Depth buffer�������յ�zֵ��[-1,1]ӳ�䵽[0,1]�ķ�Χ���������nΪ1, fΪ0����nԽ�������ֵԽ��                    
                    //����Զ����zֵΪ0�����clearʱ���Ҫ���Ϊ0��Ȼ����Ȳ���ʱ��ʹ��GREATER���ԡ�
                    //(��Ȼ����Ҳ�����������תzֵ��Ȼ��clearʱʹ��float.MaxValue�����������Ȳ���ʱʹ��LESS_EQUAL����)
                    //ע�⣺�����zֵ���������Ǳ�Ҫ�ģ�ֻ��Ϊ�˿��ӻ�ʱ����ӳ��Ϊ��ɫֵ����ʵҲ�����ڿ��ӻ��ĵط�������
                    //������ô���������ú�Unity��DirectXƽ̨��Reverse zһ������near plane������zֵ�ĸ�����������ߡ�
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

                //���ö���ɫ,ʹ��config�е���ɫ����ѭ������                
                if(_config.VertexColors != null && _config.VertexColors.Colors.Length > 0)
                {
                    int vertexColorCount = _config.VertexColors.Colors.Length;

                    t.Vertex0.Color = _config.VertexColors.Colors[idx0 % vertexColorCount];
                    t.Vertex1.Color = _config.VertexColors.Colors[idx1 % vertexColorCount];
                    t.Vertex2.Color = _config.VertexColors.Colors[idx2 % vertexColorCount];
                }
                else
                {
                    t.Vertex0.Color = Color.white;
                    t.Vertex1.Color = Color.white;
                    t.Vertex2.Color = Color.white;
                }

                //set world space pos & normal
                t.Vertex0.WorldPos = vsOutput[idx0].worldPos;
                t.Vertex1.WorldPos = vsOutput[idx1].worldPos;
                t.Vertex2.WorldPos = vsOutput[idx2].worldPos;
                t.Vertex0.WorldNormal = vsOutput[idx0].worldNormal;
                t.Vertex1.WorldNormal = vsOutput[idx1].worldNormal;
                t.Vertex2.WorldNormal = vsOutput[idx2].worldNormal;

                /// ---------- Rasterization -----------
                if (_config.WireframeMode)
                {                    
                    RasterizeWireframe(t);                    
                }
                else
                {                    
                    RasterizeTriangle(t, ro);                    
                }
                
            }

            ProfileManager.EndSample();

            //Resolve AA
            if(_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
            {
                int MSAALevel = (int)_config.MSAA;
                int SamplersPerPixel = MSAALevel * MSAALevel;

                for (int y=0; y < _height; ++y)
                {
                    for(int x=0; x < _width; ++x)
                    {
                        int index = GetIndex(x, y);
                        Color color = Color.clear;
                        float a = 0.0f;
                        for(int si=0; si < MSAALevel; ++si)
                        {
                            for(int sj=0; sj < MSAALevel; ++sj)
                            {
                                int xi = x * MSAALevel + si;
                                int yi = y * MSAALevel + sj;
                                int indexSamper = yi * _width * MSAALevel + xi;
                                if (samplers_mask_MSAA[indexSamper])
                                {
                                    color += samplers_color_MSAA[indexSamper];
                                    a += 1.0f;
                                }
                            }
                        }
                        if(a > 0.0f)
                        {
                            frame_buf[index] = color / SamplersPerPixel;
                        }
                    }
                }
            }

            ProfileManager.EndSample();
        }        

        //������Clipping���������ڲ�����clipping volume�е�ͼԪ��
        //Ӳ��ʵ��ʱһ��ֻ�Բ��ֶ���zֵ��near,far֮���ͼԪ����clipping������
        //�����ֶ���x,yֵ��x,y�ü�ƽ��֮���ͼԪ�򲻽��вü���ֻ��ͨ��һ����viewport����һЩ��guard-band������������޳����൱�ڷŴ�x,y�Ĳ��Է�Χ��
        //����x,y�ü�ƽ��֮���ͼԪ������frame buffer�Ͻ���Scissor���ԡ�
        //�˴���ʵ�ּ�Ϊֻ�������׶�޳��������κ�clipping����������x,y�ü�û���⣬��Ȼû����region,Ҳ���������frame buffer�ϲü�����
        //����z�Ĳü�����û�д����ῴ��������������ʧ���µı�Ե������

        //ֱ��ʹ��Clip space�µ���׶�޳��㷨   
        [MethodImpl(MethodImplOptions.AggressiveInlining)]                 
        bool Clipped(Vector4[] v)
        {            
            //�ֱ�����׶��������棬��������������������㶼��ĳ����֮�⣬�������������׶�⣬�޳�  
            //����NDC����������-1<=Zndc<=1, ���� w < 0 ʱ��-w >= Zclip = Zndc*w >= w�����Դ�ʱclip space�����귶Χ��[w,-w], Ϊ�˱Ƚ�ʱ����ȷ����wȡ��      
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

        //Clipped_old���㷨ֻ��鶥���Ƿ�����׶�ڡ����ǵ��������ر�󣬴�����׶�����ж��㶼����׶��ʱ���������Ϊ��Ҫ�޳��� (false negative)
        bool Clipped_old(Vector4[] v)
        {
            //Clip spaceʹ��GAMES101�淶����������ϵ��nΪ+1�� fΪ-1
            //�ü����������޳���     
            //ʵ�ʵ�Ӳ������Clip space�ü������Դ˴�����Ҳʹ��clip space
            for (int i = 0; i < 3; ++i)
            {
                var vertex = v[i];
                var w = vertex.w;
                w = w >= 0 ? w : -w;
                
                bool inside = (vertex.x <= w && vertex.x >= -w
                    && vertex.y <= w && vertex.y >= -w
                    && vertex.z <= w && vertex.z >= -w);
                
                if (inside)
                {             
                    //���ü������Σ�ֻҪ������һ����clip space�������������屣��                    
                    return false;
                }
            }                

            //�������㶼���������������޳�
            return true;
        }

        #region Wireframe mode
        //Breshham�㷨����,��ɫʹ�����Բ�ֵ����͸��У����
        private void DrawLine(Vector3 begin, Vector3 end, Color colorBegin, Color colorEnd)
        {            
            int x1 = Mathf.FloorToInt(begin.x);
            int y1 = Mathf.FloorToInt(begin.y);
            int x2 = Mathf.FloorToInt(end.x);
            int y2 = Mathf.FloorToInt(end.y);            

            int x, y, dx, dy, dx1, dy1, px, py, xe, ye, i;

            dx = x2 - x1;
            dy = y2 - y1;
            dx1 = Math.Abs(dx);
            dy1 = Math.Abs(dy);
            px = 2 * dy1 - dx1;
            py = 2 * dx1 - dy1;

            Color c1 = colorBegin;
            Color c2 = colorEnd;

            if (dy1 <= dx1)
            {
                if (dx >= 0)
                {
                    x = x1;
                    y = y1;
                    xe = x2;
                }
                else
                {
                    x = x2;
                    y = y2;
                    xe = x1;
                    c1 = colorEnd;
                    c2 = colorBegin;
                }
                Vector3 point = new Vector3(x, y, 1.0f);                 
                SetPixel(point, c1);
                for (i = 0; x < xe; i++)
                {
                    x++;
                    if (px < 0)
                    {
                        px += 2 * dy1;
                    }
                    else
                    {
                        if ((dx < 0 && dy < 0) || (dx > 0 && dy > 0))
                        {
                            y++;
                        }
                        else
                        {
                            y--;
                        }
                        px +=  2 * (dy1 - dx1);
                    }
                    
                    Vector3 pt = new Vector3(x, y, 1.0f);
                    float t = 1.0f - (float)(xe - x) / dx1;
                    Color line_color = Color.Lerp(c1, c2, t);                    
                    SetPixel(pt, line_color);
                }
            }
            else
            {
                if (dy >= 0)
                {
                    x = x1;
                    y = y1;
                    ye = y2;
                }
                else
                {
                    x = x2;
                    y = y2;
                    ye = y1;
                    c1 = colorEnd;
                    c2 = colorBegin;
                }
                Vector3 point = new Vector3(x, y, 1.0f);                
                SetPixel(point, c1);
                
                for (i = 0; y < ye; i++)
                {
                    y++;
                    if (py <= 0)
                    {
                        py += 2 * dx1;
                    }
                    else
                    {
                        if ((dx < 0 && dy < 0) || (dx > 0 && dy > 0))
                        {
                            x++;
                        }
                        else
                        {
                            x--;
                        }
                        py += 2 * (dx1 - dy1);
                    }
                    Vector3 pt = new Vector3(x, y, 1.0f);
                    float t = 1.0f - (float)(ye - y) / dy1;
                    Color line_color = Color.Lerp(c1, c2, t);
                    SetPixel(pt, line_color);
                }
            }
        }

        private void RasterizeWireframe(Triangle t)
        {
            ProfileManager.BeginSample("CPURasterizer.RasterizeWireframe");
            DrawLine(t.Vertex0.Position, t.Vertex1.Position, t.Vertex0.Color, t.Vertex1.Color);
            DrawLine(t.Vertex1.Position, t.Vertex2.Position, t.Vertex1.Color, t.Vertex2.Color);
            DrawLine(t.Vertex2.Position, t.Vertex0.Position, t.Vertex2.Color, t.Vertex0.Color);
            ProfileManager.EndSample();
        }

        #endregion

        

        //Screen space  rasterization
        void RasterizeTriangle(Triangle t, RenderingObject ro)
        {
            ProfileManager.BeginSample("CPURasterizer.RasterizeTriangle");
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

            if(_config.MSAA == MSAALevel.Disabled)
            {                
                // ������ǰ�����ΰ�Χ�е��������أ��жϵ�ǰ�����Ƿ�����������
                // �������������е����أ�ʹ�����������ֵ�õ����ֵ����ʹ��z buffer������Ȳ��Ժ�д��
                for(int y = minPY; y < maxPY; ++y)
                {
                    for(int x = minPX; x < maxPX; ++x)
                    {
                        //if(IsInsideTriangle(x, y, t)) //-->����Ƿ����������ڱ�ʹ������������Ҫ��������ȼ����������꣬�ټ��3�������Ƿ���С��0
                        {
                            //������������
                            var c = ComputeBarycentric2D(x, y, t);
                            float alpha = c.x;
                            float beta = c.y;
                            float gamma = c.z;
                            if(alpha < 0 || beta < 0 || gamma < 0){                                
                                continue;
                            }
                            //͸��У����ֵ��zΪ͸��У����ֵ���view space zֵ
                            float z = 1.0f / (alpha / v[0].w + beta / v[1].w + gamma / v[2].w);
                            //zpΪ͸��У����ֵ���screen space zֵ
                            float zp = (alpha * v[0].z / v[0].w + beta * v[1].z / v[1].w + gamma * v[2].z / v[2].w) * z;
                            
                            //��Ȳ���(ע�����������zֵԽ��Խ����near plane����˴�ֵͨ�����ԣ�
                            int index = GetIndex(x, y);
                            if(zp >= depth_buf[index])
                            {
                                depth_buf[index] = zp;
                                
                                //͸��У����ֵ
                                ProfileManager.BeginSample("CPURasterizer.AttributeInterpolation");
                                Color color_p = (alpha * t.Vertex0.Color / v[0].w + beta * t.Vertex1.Color / v[1].w + gamma * t.Vertex2.Color / v[2].w) * z;
                                Vector2 uv_p = (alpha * t.Vertex0.Texcoord / v[0].w + beta * t.Vertex1.Texcoord / v[1].w + gamma * t.Vertex2.Texcoord / v[2].w) * z;
                                Vector3 normal_p = (alpha * t.Vertex0.Normal / v[0].w + beta * t.Vertex1.Normal  / v[1].w + gamma * t.Vertex2.Normal  / v[2].w) * z;
                                Vector3 worldPos_p = (alpha * t.Vertex0.WorldPos / v[0].w + beta * t.Vertex1.WorldPos / v[1].w + gamma * t.Vertex2.WorldPos / v[2].w) * z;
                                Vector3 worldNormal_p = (alpha * t.Vertex0.WorldNormal / v[0].w + beta * t.Vertex1.WorldNormal / v[1].w + gamma * t.Vertex2.WorldNormal / v[2].w) * z;
                                ProfileManager.EndSample();

                         
                                    FragmentShaderInputData input = new FragmentShaderInputData();
                                    input.Color = color_p;
                                    input.UV = uv_p;
                                    input.TextureData = ro.texture.GetPixelData<URColor24>(0);
                                    input.TextureWidth = ro.texture.width;
                                    input.TextureHeight = ro.texture.height;
                                    input.UseBilinear = _config.BilinearSample;
                                    input.LocalNormal = normal_p;
                                    input.WorldPos = worldPos_p;
                                    input.WorldNormal = worldNormal_p;

                                    ProfileManager.BeginSample("CPURasterizer.FragmentShader");
                                    switch(_config.FragmentShaderType){
                                        case ShaderType.BlinnPhong:
                                            frame_buf[index] = ShaderContext.FSBlinnPhong(input, Uniforms);
                                            break;
                                        case ShaderType.NormalVisual:
                                            frame_buf[index] = ShaderContext.FSNormalVisual(input);
                                            break;
                                        case ShaderType.VertexColor:
                                            frame_buf[index] = ShaderContext.FSVertexColor(input);
                                            break;
                                    }
                                    
                                    ProfileManager.EndSample();                                                                                                
                            }
                        }                        
                    }
                }
            }
            else
            {
                int MSAALevel = (int)_config.MSAA;
                float sampler_dis = 1.0f / MSAALevel;
                float sampler_dis_half = sampler_dis * 0.5f;

                for (int y = minPY; y < maxPY; ++y)
                {
                    for (int x = minPX; x < maxPX; ++x)
                    {
                        //���ÿ���������Ƿ����������ڣ�����ڽ������������ֵ����Ȳ���
                        for(int si=0; si<MSAALevel; ++si)
                        {
                            for(int sj=0; sj<MSAALevel; ++sj)
                            {
                                float offsetx = sampler_dis_half + si * sampler_dis;
                                float offsety = sampler_dis_half + sj * sampler_dis;
                                //if (IsInsideTriangle(x, y, t, offsetx, offsety))
                                {
                                    //������������
                                    var c = ComputeBarycentric2D(x+offsetx, y+offsety, t);
                                    float alpha = c.x;
                                    float beta = c.y;
                                    float gamma = c.z;
                                    if(alpha < 0 || beta < 0 || gamma < 0){                                
                                        continue;
                                    }
                                    //͸��У����ֵ��zΪ͸��У����ֵ���view space zֵ
                                    float z = 1.0f / (alpha / v[0].w + beta / v[1].w + gamma / v[2].w);
                                    //zpΪ͸��У����ֵ���screen space zֵ
                                    float zp = (alpha * v[0].z / v[0].w + beta * v[1].z / v[1].w + gamma * v[2].z / v[2].w) * z;

                                    //��Ȳ���(ע�����������zֵԽ��Խ����near plane����˴�ֵͨ�����ԣ�                                    
                                    int xi = x * MSAALevel + si;
                                    int yi = y * MSAALevel + sj;
                                    int index = yi * _width * MSAALevel + xi;
                                    if (zp > samplers_depth_MSAA[index])
                                    {
                                        samplers_depth_MSAA[index] = zp;
                                        samplers_mask_MSAA[index] = true;

                                        //͸��У����ֵ
                                        Color color_p = (alpha * t.Vertex0.Color / v[0].w + beta * t.Vertex1.Color / v[1].w + gamma * t.Vertex2.Color / v[2].w) * z;
                                        samplers_color_MSAA[index] = color_p;
                                    }
                                }
                            }
                        }
                        
                    }
                }
            }

            ProfileManager.EndSample();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsInsideTriangle(int x, int y, Triangle t, float offsetX=0.5f, float offsetY=0.5f)
        {
            ProfileManager.BeginSample("CPURasterizer.IsInsideTriangle");
            var v = _tmpVector3s;            
            v[0] = new Vector3(t.Vertex0.Position.x, t.Vertex0.Position.y, t.Vertex0.Position.z);
            v[1] = new Vector3(t.Vertex1.Position.x, t.Vertex1.Position.y, t.Vertex1.Position.z);
            v[2] = new Vector3(t.Vertex2.Position.x, t.Vertex2.Position.y, t.Vertex2.Position.z);            

            //��ǰ��������λ��p
            Vector3 p = new Vector3(x + offsetX, y + offsetY, 0);            
            
            Vector3 v0p = p - v[0]; v0p[2] = 0;
            Vector3 v01 = v[1] - v[0]; v01[2] = 0;
            Vector3 cross0p = Vector3.Cross(v0p, v01);

            Vector3 v1p = p - v[1]; v1p[2] = 0;
            Vector3 v12 = v[2] - v[1]; v12[2] = 0;
            Vector3 cross1p = Vector3.Cross(v1p, v12);

            if(cross0p.z * cross1p.z > 0)
            {
                Vector3 v2p = p - v[2]; v2p[2] = 0;
                Vector3 v20 = v[0] - v[2]; v20[2] = 0;
                Vector3 cross2p = Vector3.Cross(v2p, v20);
                if(cross2p.z * cross1p.z > 0)
                {
                    ProfileManager.EndSample();
                    return true;
                }
            }

            ProfileManager.EndSample();
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        Vector3 ComputeBarycentric2D(float x, float y, Triangle t)
        {
            ProfileManager.BeginSample("CPURasterizer.ComputeBarycentric2D");
            var v = _tmpVector4s;            
            v[0] = t.Vertex0.Position;
            v[1] = t.Vertex1.Position;
            v[2] = t.Vertex2.Position;
            
            float c1 = (x * (v[1].y - v[2].y) + (v[2].x - v[1].x) * y + v[1].x * v[2].y - v[2].x * v[1].y) / (v[0].x * (v[1].y - v[2].y) + (v[2].x - v[1].x) * v[0].y + v[1].x * v[2].y - v[2].x * v[1].y);
            float c2 = (x * (v[2].y - v[0].y) + (v[0].x - v[2].x) * y + v[2].x * v[0].y - v[0].x * v[2].y) / (v[1].x * (v[2].y - v[0].y) + (v[0].x - v[2].x) * v[1].y + v[2].x * v[0].y - v[0].x * v[2].y);
            float c3 = (x * (v[0].y - v[1].y) + (v[1].x - v[0].x) * y + v[0].x * v[1].y - v[1].x * v[0].y) / (v[2].x * (v[0].y - v[1].y) + (v[1].x - v[0].x) * v[2].y + v[0].x * v[1].y - v[1].x * v[0].y);
            
            ProfileManager.EndSample();
            return new Vector3(c1, c2, c3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetIndex(int x, int y)
        {
            return y * _width + x;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetPixel(Vector3 point, Color color)
        {
            if(point.x < 0 || point.x >= _width || point.y < 0 || point.y >= _height)
            {
                return;
            }

            int idx = (int)point.y * _width + (int)point.x;
            frame_buf[idx] = color;
        }

        public void UpdateFrame()
        {
            ProfileManager.BeginSample("CPURasterizer.UpdateFrame");

            switch (_config.DisplayBuffer)
            {
                case DisplayBufferType.Color:
                    texture.SetPixels(frame_buf);
                    break;
                case DisplayBufferType.DepthRed:
                case DisplayBufferType.DepthGray:
                    for (int i = 0; i < depth_buf.Length; ++i)
                    {
                        //depth_buf�е�ֵ��Χ��[0,1]���������Ϊ1����Զ��Ϊ0����˿��ӻ��󱳾��Ǻ�ɫ
                        float c = depth_buf[i]; 
                        if(_config.DisplayBuffer == DisplayBufferType.DepthRed)
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

            if (StatDelegate != null)
            {
                StatDelegate(_verticesAll, _trianglesAll, _trianglesRendered);
            }

            ProfileManager.EndSample();
        }


    }
}