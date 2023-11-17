using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;

namespace GPUDrivenTerrainLearn
{
    public enum LoadingState : sbyte
    {
        NotLoad,
        Loading,
        DoneLoad,
    }
    public class TerrainBuilder:System.IDisposable
    {
        private ComputeShader _computeShader;

        private ComputeBuffer _maxLODNodeList;
        private ComputeBuffer _nodeListA;
        private ComputeBuffer _nodeListB;
        private ComputeBuffer _finalNodeListBuffer;
        private ComputeBuffer _nodeDescriptors;

        private ComputeBuffer _culledPatchBuffer;

        private ComputeBuffer _patchIndirectArgs;
        private ComputeBuffer _patchBoundsBuffer;
        private ComputeBuffer _patchBoundsIndirectArgs; 
        private ComputeBuffer _indirectArgsBuffer;
        private RenderTexture _lodMap;

        private const int PatchStripSize = 14*4;

        private Vector4 _nodeEvaluationC = new Vector4(1,0,0,0);
        private bool _isNodeEvaluationCDirty = true;

        private float _hizDepthBias = 1;

        private TerrainAsset _asset;

        private CommandBuffer _commandBuffer = new CommandBuffer();
        private Plane[] _cameraFrustumPlanes = new Plane[6];
        private Vector4[] _cameraFrustumPlanesV4 = new Vector4[6];

        private int _kernelOfTraverseQuadTree;
        private int _kernelOfBuildLodMap;
        private int _kernelOfBuildPatches;

        /// <summary>
        /// Buffer的大小需要根据预估的最大分割情况进行分配.
        /// </summary>
        private int _maxNodeBufferSize = 200;
        private int _tempNodeBufferSize = 50;

        public Vector3 GetWorldSize()
        {
            return _asset.worldSize;
        }
        public TerrainBuilder(TerrainAsset asset){
            _asset = asset;
            _computeShader = asset.computeShader;
            _commandBuffer.name = "TerrainBuild";
            _culledPatchBuffer = new ComputeBuffer(_maxNodeBufferSize * 64,PatchStripSize,ComputeBufferType.Append);
            
            _patchIndirectArgs = new ComputeBuffer(5,4,ComputeBufferType.IndirectArguments);
            _patchIndirectArgs.SetData(new uint[]{TerrainAsset.patchMesh.GetIndexCount(0),0,0,0,0});

            _patchBoundsIndirectArgs = new ComputeBuffer(5,4,ComputeBufferType.IndirectArguments);
            _patchBoundsIndirectArgs.SetData(new uint[]{TerrainAsset.unitCubeMesh.GetIndexCount(0),0,0,0,0});

            _maxLODNodeList = new ComputeBuffer(asset.MAX_LOD_NODE_COUNT * asset.MAX_LOD_NODE_COUNT,8,ComputeBufferType.Append);
            this.InitMaxLODNodeListDatas();

            _nodeListA = new ComputeBuffer(_tempNodeBufferSize,8,ComputeBufferType.Append);
            _nodeListB = new ComputeBuffer(_tempNodeBufferSize,8,ComputeBufferType.Append);
            _indirectArgsBuffer = new ComputeBuffer(3,4,ComputeBufferType.IndirectArguments);
            _indirectArgsBuffer.SetData(new uint[]{1,1,1});
            _finalNodeListBuffer = new ComputeBuffer(_maxNodeBufferSize,12,ComputeBufferType.Append);
            _nodeDescriptors = new ComputeBuffer((int)(asset.MAX_NODE_ID + 1),4);
            
            _patchBoundsBuffer = new ComputeBuffer(_maxNodeBufferSize * 64,4*10,ComputeBufferType.Append);

            _lodMap = TextureUtility.CreateLODMap(_asset.GetLodMapSize());
            
            if(SystemInfo.usesReversedZBuffer){
                _computeShader.EnableKeyword("_REVERSE_Z");
            }else{
                _computeShader.DisableKeyword("_REVERSE_Z");
            }

            this.InitKernels();
            this.InitWorldParams();

            this.boundsHeightRedundance = 5;
            this.hizDepthBias = 1;
        }

        private void InitMaxLODNodeListDatas(){
            var maxLODNodeCount = _asset.MAX_LOD_NODE_COUNT;
            //最大lod时候的世界的区块的
            uint2[] datas = new uint2[maxLODNodeCount * maxLODNodeCount];
            var index = 0;
            for(uint i = 0; i < maxLODNodeCount; i ++){
                for(uint j = 0; j < maxLODNodeCount; j ++){
                    datas[index] = new uint2(i,j);
                    index ++;
                }
            }
            _maxLODNodeList.SetData(datas);
        }

        private void InitKernels(){
            _kernelOfTraverseQuadTree = _computeShader.FindKernel("TraverseQuadTree");
            _kernelOfBuildLodMap = _computeShader.FindKernel("BuildLodMap");
            _kernelOfBuildPatches = _computeShader.FindKernel("BuildPatches");
            this.BindComputeShader(_kernelOfTraverseQuadTree);
            this.BindComputeShader(_kernelOfBuildLodMap);
            this.BindComputeShader(_kernelOfBuildPatches);
        }
        private void BindComputeShader(int kernelIndex){
            //_computeShader.SetTexture(kernelIndex,"QuadTreeTexture",_asset.quadTreeMap);
           
            
            if(kernelIndex == _kernelOfTraverseQuadTree){
                _computeShader.SetBuffer(kernelIndex,ShaderConstants.AppendFinalNodeList,_finalNodeListBuffer);
                _computeShader.SetTexture(kernelIndex,"MinMaxHeightTexture",_asset.minMaxHeightMap);
                _computeShader.SetBuffer(kernelIndex,ShaderConstants.NodeDescriptors,_nodeDescriptors);
            }else if(kernelIndex == _kernelOfBuildLodMap){
                _computeShader.SetTexture(kernelIndex,ShaderConstants.LodMap,_lodMap);
                _computeShader.SetBuffer(kernelIndex,ShaderConstants.NodeDescriptors,_nodeDescriptors);
            }
            else if(kernelIndex == _kernelOfBuildPatches){
                _computeShader.SetTexture(kernelIndex,ShaderConstants.LodMap,_lodMap);
                _computeShader.SetTexture(kernelIndex,"MinMaxHeightTexture",_asset.minMaxHeightMap);
                _computeShader.SetBuffer(kernelIndex,ShaderConstants.FinalNodeList,_finalNodeListBuffer);
                _computeShader.SetBuffer(kernelIndex,"CulledPatchList",_culledPatchBuffer);
                _computeShader.SetBuffer(kernelIndex,"PatchBoundsList",_patchBoundsBuffer);
            }
        }
        /// <summary>
        /// 初始化世界参数
        /// </summary>
        private void InitWorldParams(){
            float wSize = _asset.worldSize.x;
            int nodeCount = _asset.MAX_LOD_NODE_COUNT;
            Vector4[] worldLODParams = new Vector4[_asset.MAX_LOD + 1];
            for(var lod = _asset.MAX_LOD; lod >=0; lod --){
                var nodeSize = wSize / nodeCount;
                var patchExtent = nodeSize / 16;
                var sectorCountPerNode = (int)Mathf.Pow(2,lod);
                worldLODParams[lod] = new Vector4(nodeSize,patchExtent,nodeCount,sectorCountPerNode);
                nodeCount *= 2;
            }
            _computeShader.SetVectorArray(ShaderConstants.WorldLodParams,worldLODParams);

            int[] nodeIDOffsetLOD = new int[ (_asset.MAX_LOD + 1) * 4];
            int nodeIdOffset = 0;
            for(int lod = _asset.MAX_LOD; lod >=0; lod --){
                nodeIDOffsetLOD[lod * 4] = nodeIdOffset;
                //{20,0} ,{16,25} ,{12,125},{8,525},{4,2125},{0,8525}
                nodeIdOffset += (int)(worldLODParams[lod].z * worldLODParams[lod].z);
            }
            _computeShader.SetInts("NodeIDOffsetOfLOD",nodeIDOffsetLOD);
            //_computeShader.SetInt("MAX_TERRAIN_LOD",_asset.MAX_LOD);
            _computeShader.SetInt("SECTOR_COUNT_WORLD",_asset.GetSECTOR_COUNT_WORLD());

        }


        public int boundsHeightRedundance{
            set{
                _computeShader.SetInt("_BoundsHeightRedundance",value);
            }
        }

        public float nodeEvalDistance{
            set{
                _nodeEvaluationC.x = value;
                _isNodeEvaluationCDirty = true;
            }
        }

        public bool enableSeamDebug{
            set{
                if(value){
                    _computeShader.EnableKeyword("ENABLE_SEAM");
                }else{
                    _computeShader.DisableKeyword("ENABLE_SEAM");
                }
            }
        }

        public float hizDepthBias{
            set{
                _hizDepthBias = value;
                _computeShader.SetFloat("_HizDepthBias",Mathf.Clamp(value, 0.01f,1000f));
            }get{
                return _hizDepthBias;
            }
        }

        public void Dispose()
        {
            _culledPatchBuffer.Dispose();
            _patchIndirectArgs.Dispose();
            _finalNodeListBuffer.Dispose();
            _maxLODNodeList.Dispose();
            _nodeListA.Dispose();
            _nodeListB.Dispose();
            _indirectArgsBuffer.Dispose();
            _patchBoundsBuffer.Dispose();
            _patchBoundsIndirectArgs.Dispose();
            _nodeDescriptors.Dispose();
        }



        public bool isFrustumCullEnabled{
            set{
                if(value){
                    _computeShader.EnableKeyword("ENABLE_FRUS_CULL");
                }else{
                    _computeShader.DisableKeyword("ENABLE_FRUS_CULL");
                }
            }
        }

        public bool isHizOcclusionCullingEnabled{
            set{
                if(value){
                    _computeShader.EnableKeyword("ENABLE_HIZ_CULL");
                }else{
                    _computeShader.DisableKeyword("ENABLE_HIZ_CULL");
                }
            }
        }

        private bool _isBoundsBufferOn;

        public bool isBoundsBufferOn{
            set{
                if(value){
                    _computeShader.EnableKeyword("BOUNDS_DEBUG");
                }else{
                    _computeShader.DisableKeyword("BOUNDS_DEBUG");
                }
                _isBoundsBufferOn = value;
            }get{
                return _isBoundsBufferOn;
            }
        }

        private void LogPatchArgs(){
            var data = new uint[5];
            _patchIndirectArgs.GetData(data);
            Debug.Log(data[1]);
        }

 

        private void ClearBufferCounter(){
            _commandBuffer.SetBufferCounterValue(_maxLODNodeList,(uint)_maxLODNodeList.count);
            _commandBuffer.SetBufferCounterValue(_nodeListA,0);
            _commandBuffer.SetBufferCounterValue(_nodeListB,0);
            _commandBuffer.SetBufferCounterValue(_finalNodeListBuffer,0);
            _commandBuffer.SetBufferCounterValue(_culledPatchBuffer,0);
            _commandBuffer.SetBufferCounterValue(_patchBoundsBuffer,0);
        }

        private void UpdateCameraFrustumPlanes(Camera camera){
            GeometryUtility.CalculateFrustumPlanes(camera,_cameraFrustumPlanes);
            for(var i = 0; i < _cameraFrustumPlanes.Length; i ++){
                Vector4 v4 = (Vector4)_cameraFrustumPlanes[i].normal;
                v4.w = _cameraFrustumPlanes[i].distance;
                _cameraFrustumPlanesV4[i] = v4;
            }
            _computeShader.SetVectorArray(ShaderConstants.CameraFrustumPlanes,_cameraFrustumPlanesV4);
        }
        //执行渲染
        public void Dispatch(){
            var camera = Camera.main;
            //clear
            _commandBuffer.Clear();
            this.ClearBufferCounter();
            //设置摄像机裁剪平面
            this.UpdateCameraFrustumPlanes(camera);
            
            SetComputeBufferParam(camera);
            TraverseQuadTree();
            //生成LodMap
            _commandBuffer.DispatchCompute(_computeShader,_kernelOfBuildLodMap,_asset.GetLodMapSize()/8,_asset.GetLodMapSize()/8,1);
            //生成Patch
            _commandBuffer.CopyCounterValue(_finalNodeListBuffer,_indirectArgsBuffer,0);
            _commandBuffer.DispatchCompute(_computeShader,_kernelOfBuildPatches,_indirectArgsBuffer,0);
            _commandBuffer.CopyCounterValue(_culledPatchBuffer,_patchIndirectArgs,4);
            //_culledPatchBuffer.GetData(culledPatches);
            if(isBoundsBufferOn){
                _commandBuffer.CopyCounterValue(_patchBoundsBuffer,_patchBoundsIndirectArgs,4);
            }
            
            Graphics.ExecuteCommandBuffer(_commandBuffer);
            Test();

            // this.LogPatchArgs();
        }

        private void TraverseQuadTree()
        {
            ComputeBuffer consumeNodeList = _nodeListA;
            ComputeBuffer appendNodeList = _nodeListB;
            ///从最大lod开始分割，起始_maxLODNodeList为（0，0）=》（5，5）
            for (var lod = _asset.MAX_LOD; lod >= 0; lod--)
            {
                _commandBuffer.SetComputeIntParam(_computeShader, ShaderConstants.PassLOD, lod);
                if (lod == _asset.MAX_LOD)
                {
                    _commandBuffer.SetComputeBufferParam(_computeShader, _kernelOfTraverseQuadTree,
                        ShaderConstants.ConsumeNodeList, _maxLODNodeList);
                }
                else
                {
                    _commandBuffer.SetComputeBufferParam(_computeShader, _kernelOfTraverseQuadTree,
                        ShaderConstants.ConsumeNodeList, consumeNodeList);
                }

                _commandBuffer.SetComputeBufferParam(_computeShader, _kernelOfTraverseQuadTree,
                    ShaderConstants.AppendNodeList, appendNodeList);
                _commandBuffer.DispatchCompute(_computeShader, _kernelOfTraverseQuadTree, _indirectArgsBuffer, 0);
                _commandBuffer.CopyCounterValue(appendNodeList, _indirectArgsBuffer, 0);
                (consumeNodeList, appendNodeList) = (appendNodeList, consumeNodeList);
            }
        }

        private void SetComputeBufferParam(Camera camera)
        {
            if (_isNodeEvaluationCDirty)
            {
                _isNodeEvaluationCDirty = false;
                _commandBuffer.SetComputeVectorParam(_computeShader, ShaderConstants.NodeEvaluationC, _nodeEvaluationC);
            }

            //设置相机世界坐标的位置
            _commandBuffer.SetComputeVectorParam(_computeShader, ShaderConstants.CameraPositionWS, camera.transform.position);
            //设置大世界的大小
            _commandBuffer.SetComputeVectorParam(_computeShader, ShaderConstants.WorldSize, _asset.worldSize);
            //四叉树分割计算得到初步的Patch列表
            _commandBuffer.CopyCounterValue(_maxLODNodeList, _indirectArgsBuffer, 0);
        }

        private void Test()
        {
            ComputeBuffer.CopyCount(_culledPatchBuffer, counterBuffer, 0);
            _culledPatchBuffer.GetData(culledPatches);
            counterBuffer.GetData(counter);
            if (count != counter[0])
            {
                terrainPatches.Clear();
                if (count > maxCount)
                    maxCount = count;
                count = counter[0];
                Debug.Log($"now count{count} maxCount{maxCount}");
                var validData = culledPatches.Take(count);


                foreach (var patchData in validData)
                {
                    var position = patchData.position;
                    var xIndex = position.x / 512 - 5;
                    var yIndex = position.y / 512 - 5;
                    int Index = (int)(xIndex + yIndex * 10);
                    if (!terrainPatches.ContainsKey(Index))
                    {
                        terrainPatches[Index] = new TerrainPatch()
                        {
                            Index = Index,
                            X = xIndex,
                            Y = yIndex,
                            Lod = patchData.lod
                        };
                    }
                }

                foreach (var (key, terrainPatch) in terrainPatches)
                {
                    //Debug.Log($"<color=green>Lod{terrainPatch.Lod} Pos({terrainPatch.X},{terrainPatch.Y})</color>");
                }

                
                for (int i = 5; i >= 0; i--)
                {
                    var dictionary = terrainPatches;
                    var lodCount = dictionary.Count(x => x.Value.Lod == i);
                    var max = maxCounts[i];
                    if (lodCount > max && lodCount < 2000)
                    {
                        maxCounts[i] = lodCount;
                    }
                    //Debug.Log($"Lod{i}:{lodCount} MaxCount{max}");
                }
            }
        }

        private int[] maxCounts = new int[6];
        ComputeBuffer counterBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.IndirectArguments);
        int[] counter = new int[1];
        private int count,maxCount;
        private RenderPatch[] culledPatches = new RenderPatch[12800];
        public struct RenderPatch{
            public float2 position;
            public float2 minMaxHeight;
            public uint lod;

            /**
     * \brief 上下左右的lod差值
     */
            public uint4 lodTrans;
            uint4 nodeLocXYAndPatchOffsetZW;
            float perNodeUV;
        };

        private Dictionary<int,TerrainPatch> terrainPatches = new Dictionary<int,TerrainPatch>();
        public struct TerrainPatch
        {
            public int Index;
            public float X;
            public float Y;
            public uint Lod;
        }
       
        public ComputeBuffer patchIndirectArgs{
            get{
                return _patchIndirectArgs;
            }
        }

        public ComputeBuffer culledPatchBuffer{
            get{
                return _culledPatchBuffer;
            }
        }

        public ComputeBuffer nodeIDList{
            get{
                return _finalNodeListBuffer;
            }
        }
        public ComputeBuffer patchBoundsBuffer{
            get{
                return _patchBoundsBuffer;
            }
        }

        public ComputeBuffer boundsIndirectArgs{
            get{
                return _patchBoundsIndirectArgs;
            }
        }


        private class ShaderConstants{

            public static readonly int WorldSize = Shader.PropertyToID("_WorldSize");
            public static readonly int CameraPositionWS = Shader.PropertyToID("_CameraPositionWS");
            public static readonly int CameraFrustumPlanes = Shader.PropertyToID("_CameraFrustumPlanes");
            public static readonly int PassLOD = Shader.PropertyToID("PassLOD");
            public static readonly int AppendFinalNodeList = Shader.PropertyToID("AppendFinalNodeList");
            public static readonly int FinalNodeList = Shader.PropertyToID("FinalNodeList");

            public static readonly int AppendNodeList = Shader.PropertyToID("AppendNodeList");
            public static readonly int ConsumeNodeList = Shader.PropertyToID("ConsumeNodeList");
            public static readonly int NodeEvaluationC = Shader.PropertyToID("_NodeEvaluationC");
            public static readonly int WorldLodParams = Shader.PropertyToID("WorldLodParams");

            public static readonly int NodeDescriptors = Shader.PropertyToID("NodeDescriptors");

            public static readonly int LodMap = Shader.PropertyToID("_LodMap");
        }

    }
}
