using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using HizMapGenerator;

public class MyGrassGenerator : MonoBehaviour
{
    public int GrassCountPerRaw = 30;
    private int instanceCount;
    public Mesh instanceMesh;
    public Material instanceMaterial;
    public int subMeshIndex = 0;
    public ComputeShader compute;
    
    int cachedInstanceCount = -1;
    int cachedSubMeshIndex = -1;
    int cachedGrassCountPerRaw = -1;
    int m_depthTextureSize = 0;
    uint[] args = new uint[5] { 0, 0, 0, 0, 0 };

    ComputeBuffer argsBuffer;
    ComputeBuffer localToWorldMatrixBuffer;
    ComputeBuffer cullResult;
    int ViewPortkernel;
    int Hizkernel;
    Camera mainCamera;

    private bool VerPortCulling = false;
    private bool HiZCulling = true;
    
    int cullResultBufferId, vpMatrixId, positionBufferId, hizTextureId;
    
    void Start()
    {
        mainCamera = Camera.main;
        InitComputeShader();
        InitComputeBuffer();
        InitInstancePosition();
    }

    public static int GetHiZMapSize(Camera camera){
        var screenSize = Mathf.Max(camera.pixelWidth,camera.pixelHeight);
        var textureSize = Mathf.NextPowerOfTwo(screenSize);
        return textureSize;
    }
    
    public int depthTextureSize {
        get {
            if(m_depthTextureSize == 0)
                m_depthTextureSize = Mathf.NextPowerOfTwo(Mathf.Max(Screen.width, Screen.height));
            return m_depthTextureSize;
        }
    }
    
    void InitComputeShader()
    {
        ViewPortkernel = compute.FindKernel("ViewPortCulling");
        Hizkernel = compute.FindKernel("HizCulling");
        compute.SetBool("isOpenGL", 
            Camera.main.projectionMatrix.Equals(
                GL.GetGPUProjectionMatrix(Camera.main.projectionMatrix, false)));
        vpMatrixId = Shader.PropertyToID("vpMatrix");
        hizTextureId = Shader.PropertyToID("hizTexture");
    }

    // Update is called once per frame
    void Update()
    {
        if (cachedInstanceCount != instanceCount || cachedSubMeshIndex != subMeshIndex || cachedGrassCountPerRaw != GrassCountPerRaw)
        {
            InitComputeBuffer();
            InitInstancePosition();
        }
        
        if (false)
        {
            Vector4[] planes = MyCullTool.GetFrustumPlane(mainCamera);
            
            compute.SetBuffer(ViewPortkernel,"input",localToWorldMatrixBuffer);
            cullResult.SetCounterValue(0);
            compute.SetBuffer(ViewPortkernel,"cullresult",cullResult);
            compute.SetInt("instanceCount",instanceCount);
            compute.SetVectorArray("planes",planes);
            compute.Dispatch(ViewPortkernel,  Mathf.CeilToInt((float)instanceCount / 64), 1, 1);
            
            instanceMaterial.SetBuffer("positionBuffer", cullResult);
            //实际要渲染的数量
            ComputeBuffer.CopyCount(cullResult,argsBuffer,sizeof(uint));
        }
        if (HiZCulling && HizMap.hizTexture)
        {
            Vector4[] planes = MyCullTool.GetFrustumPlane(mainCamera);
            
            compute.SetTexture(Hizkernel, hizTextureId, HizMap.hizTexture);
            compute.SetInt("depthTextureSize", depthTextureSize);
            compute.SetBuffer(Hizkernel,"input",localToWorldMatrixBuffer);
            cullResult.SetCounterValue(0);
            compute.SetBuffer(Hizkernel,"cullresult",cullResult);
            compute.SetMatrix(vpMatrixId, 
                GL.GetGPUProjectionMatrix(mainCamera.projectionMatrix, false) * mainCamera.worldToCameraMatrix);
        
            compute.SetInt("instanceCount",instanceCount);
            compute.SetVectorArray("planes",planes);
            compute.Dispatch(Hizkernel,  Mathf.CeilToInt((float)instanceCount / 64), 1, 1);
            
            instanceMaterial.SetBuffer("positionBuffer", cullResult);
            //实际要渲染的数量
            ComputeBuffer.CopyCount(cullResult,argsBuffer,sizeof(uint));
        }
        else
        {
            instanceMaterial.SetBuffer("positionBuffer", localToWorldMatrixBuffer);
            args[1] = (uint)instanceCount;
            argsBuffer.SetData(args);
        }

        Graphics.DrawMeshInstancedIndirect(instanceMesh,subMeshIndex,instanceMaterial,
            new Bounds(Vector3.zero, new Vector3(200.0f, 200.0f, 200.0f)),argsBuffer);
    }

    void InitComputeBuffer() {
        instanceCount = GrassCountPerRaw * GrassCountPerRaw;
        argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), 
            ComputeBufferType.IndirectArguments);
        
        cullResult = new ComputeBuffer(instanceCount,sizeof(float) * 16, 
            ComputeBufferType.Append);
        
        if (instanceMesh != null)
            subMeshIndex = Mathf.Clamp(subMeshIndex, 0, instanceMesh.subMeshCount - 1);
        
        if(localToWorldMatrixBuffer != null)
            localToWorldMatrixBuffer.Release();

        localToWorldMatrixBuffer = new ComputeBuffer(instanceCount, 16 * sizeof(float));
    }

    void InitInstancePosition()
    {
        const int padding = 2;
        int width = (100 - padding * 2);
        int widthStart = -width / 2;
        float step = (float)width / GrassCountPerRaw;
        
        Matrix4x4[] localToWorldMatrixs = new Matrix4x4[instanceCount];
        for(int i = 0; i < GrassCountPerRaw; i++) {
            for(int j = 0; j < GrassCountPerRaw; j++) {
                Vector2 xz = new Vector2(widthStart + step * i, widthStart + step * j);
                Vector3 position = new Vector3(xz.x, GetGroundHeight(xz), xz.y);
                localToWorldMatrixs[i * GrassCountPerRaw + j] = Matrix4x4.TRS(position, Quaternion.identity, Vector3.one);
            }
        }

        localToWorldMatrixBuffer.SetData(localToWorldMatrixs);
        instanceMaterial.SetBuffer("positionBuffer", localToWorldMatrixBuffer);

        if (instanceMesh != null)
        {
            args[0] = (uint)instanceMesh.GetIndexCount(subMeshIndex);
            args[1] = (uint)instanceCount;
            args[2] = (uint)instanceMesh.GetIndexStart(subMeshIndex);
            args[3] = (uint)instanceMesh.GetBaseVertex(subMeshIndex);
        } else {
            args[0] = args[1] = args[2] = args[3] = 0;
        }
        argsBuffer.SetData(args);

        cachedInstanceCount = instanceCount;
        cachedSubMeshIndex = subMeshIndex;
        cachedGrassCountPerRaw = GrassCountPerRaw;
    }

    //通过Raycast计算草的高度
    float GetGroundHeight(Vector2 xz) {
        RaycastHit hit;
        if(Physics.Raycast(new Vector3(xz.x, 10, xz.y), Vector3.down, out hit, 20)) {
            return 10 - hit.distance;
        }
        return 0;
    }
    
    void OnGUI() {
        GUI.Label(new Rect(265, 25, 200, 30), "Instance Count: " + instanceCount.ToString());
        GrassCountPerRaw = (int)GUI.HorizontalSlider(new Rect(25, 20, 400, 30),
            (float)GrassCountPerRaw, 100.0f, 1000.0f);
        if (GUI.Button(new Rect(25, 50, 100, 30),"<size=10>HiZCulling Culling</size>")) {
            HiZCulling = HiZCulling == false? true:false;
        }
        GUI.Label(new Rect(265, 50, 200, 30), "Culling: " + HiZCulling.ToString());
    }
    
    private void OnDisable() {
        localToWorldMatrixBuffer?.Release();
        localToWorldMatrixBuffer = null;

        cullResult?.Release();
        cullResult = null;

        argsBuffer?.Release();
        argsBuffer = null;
    }
}
