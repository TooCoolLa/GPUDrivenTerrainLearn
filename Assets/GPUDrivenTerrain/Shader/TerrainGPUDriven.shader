Shader "GPUTerrainLearn/TerrainGPUDriven"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _HeightMap ("Texture", 2D) = "white" {}
        _NormalMap ("Texture", 2D) = "white" {}
        _TerrainColorr1 ("TerrainColor1", Color) = (1, 1, 1, 1)
        _Splat0 ("Layer 0 (R)", 2D) = "white" {}
        _BumpScaleS0 ("BumpScaleSo", Range(0, 1)) = 0
        _Normal0("Normal 0 (R)", 2D) = "bump" {}
        _TerrainColorr2 ("TerrainColor2", Color) = (1, 1, 1, 1)
        _Splat1 ("Layer 1 (G)", 2D) = "white" {}
        _BumpScaleS1 ("BumpScaleS1", Range(0, 1)) = 0
        _Normal1("Normal 1 (G)", 2D) = "bump" {}
        _TerrainColorr3 ("TerrainColor3", Color) = (1, 1, 1, 1)
        _Splat2 ("Layer 2 (B)", 2D) = "white" {}
        _BumpScaleS2 ("BumpScaleS2", Range(0, 1)) = 0
        _Normal2("Normal 2 (B)", 2D) = "bump" {}
        _TerrainColorr4 ("TerrainColor4", Color) = (1, 1, 1, 1)
        _Splat3 ("Layer 3 (A)", 2D) = "white" {}
        _BumpScaleS3 ("BumpScaleS3", Range(0, 1)) = 0
        _Normal3("Normal 3 (A)", 2D) = "bump" {}
        _SmoothnessS1 ("Smoothness1", Range(0, 1)) = 0
        _SmoothnessS2 ("Smoothness2", Range(0, 1)) = 0
        _SmoothnessS3 ("Smoothness3", Range(0, 1)) = 0
        _SmoothnessS4 ("Smoothness4", Range(0, 1)) = 0
        _Control ("Control (RGBA)", 2D) = "white" {}
        _MatcapValue ("MatCapValue", Float) = 0
        _NoiseMap("Noise Map",2D) = "white"{}
        _NoiseColor("Noise Color",color) = (1,1,1,1)
        _NoiseIntensity("Noise Intensity",range(0,1)) = 1
    }
    SubShader
    {
        Tags
        {
            "RenderType"="Opaque" "LightMode" = "UniversalForward"
        }
        LOD 100

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma shader_feature ENABLE_MIP_DEBUG
            #pragma shader_feature ENABLE_PATCH_DEBUG
            #pragma shader_feature ENABLE_LOD_SEAMLESS
            #pragma shader_feature ENABLE_NODE_DEBUG

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "./CommonInput.hlsl"

            StructuredBuffer<RenderPatch> PatchList;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                uint instanceID : SV_INSTANCEID;
                float2 texcoord : TEXCOORD1;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                half3 color: TEXCOORD1;
                float2 uvglobal : TEXCOORD2;
                #if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
                half3 positionWS : TEXCOORD3;
                #endif
                half4 fogFactorAndVertexLight : TEXCOORD6;
                half4 tangentWS : TEXCOORD4;
                half3 normalWS : TEXCOORD5;
                half3 viewDirWS : TEXCOORD7;
                float4 uv1 :TEXCOORD8;
                float4 uv2 :TEXCOORD9;
                float4 uv3 :TEXCOORD10;
                DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 11);
            };

            TEXTURE2D(_MainTex) ;
            float4 _MainTex_ST;
            SAMPLER(sampler_MainTex);
            
            TEXTURE2D(_HeightMap);
            half4 _HeightMap_ST;
            SAMPLER(sampler_HeightMap);

            TEXTURE2D(_NormalMap);
            half4 _NormalMap_ST;
            SAMPLER(sampler_NormalMap);

            
            uniform float3 _WorldSize;
            float4x4 _WorldToNormalMapMatrix;

            static half3 debugColorForMip[6] = {
                half3(0, 1, 0),
                half3(0, 0, 1),
                half3(1, 0, 0),
                half3(1, 1, 0),
                half3(0, 1, 1),
                half3(1, 0, 1),
            };

            float3 TransformNormalToWorldSpace(float3 normal)
            {
                return SafeNormalize(mul(normal, (float3x3)_WorldToNormalMapMatrix));
            }

            float3 ProjectOnPlane(float3 vector4Project, float3 planeNormal)
            {
                float num1 = dot(planeNormal, planeNormal);
                if (num1 < 0.0001)
                    return vector4Project;
                float num2 = dot(vector4Project, planeNormal);
                float3 final = vector4Project - planeNormal * num2 / num1;
                return final;
            }

            float3 SampleNormal(float2 uv)
            {
                float3 normal;
                float4 color =  SAMPLE_TEXTURE2D(_NormalMap,sampler_NormalMap,uv);
                normal.xz = color.xy * 2 - 1 ; //tex2Dlod(_NormalMap, float4(uv, 0, 0)).xy * 2 - 1;
                normal.y = sqrt(max(0, 1 - dot(normal.xz, normal.xz)));
                normal = TransformNormalToWorldSpace(normal);
                return normal;
            }


            //修复接缝，只有边上的需要移动，角上的不需要移动
            void FixLODConnectSeam(inout float4 vertex, inout float2 uv, RenderPatch patch)
            {
                uint4 lodTrans = patch.lodTrans;
                uint2 vertexIndex = floor((vertex.xz + PATCH_MESH_SIZE * 0.5 + 0.01) / PATCH_MESH_GRID_SIZE);
                float uvGridStrip = 1.0 / PATCH_MESH_GRID_COUNT;
                //左
                uint lodDelta = lodTrans.x;
                if (lodDelta > 0 && vertexIndex.x == 0)
                {
                    uint gridStripCount = pow(2, lodDelta);
                    uint modIndex = vertexIndex.y % gridStripCount;
                    if (modIndex != 0)
                    {
                        vertex.z -= PATCH_MESH_GRID_SIZE * modIndex;
                        uv.y -= uvGridStrip * modIndex;
                        return;
                    }
                }
                //下
                lodDelta = lodTrans.y;
                if (lodDelta > 0 && vertexIndex.y == 0)
                {
                    uint gridStripCount = pow(2, lodDelta);
                    uint modIndex = vertexIndex.x % gridStripCount;
                    if (modIndex != 0)
                    {
                        vertex.x -= PATCH_MESH_GRID_SIZE * modIndex;
                        uv.x -= uvGridStrip * modIndex;
                        return;
                    }
                }
                //右
                lodDelta = lodTrans.z;
                if (lodDelta > 0 && vertexIndex.x == PATCH_MESH_GRID_COUNT)
                {
                    uint gridStripCount = pow(2, lodDelta);
                    uint modIndex = vertexIndex.y % gridStripCount;
                    if (modIndex != 0)
                    {
                        vertex.z += PATCH_MESH_GRID_SIZE * (gridStripCount - modIndex);
                        uv.y += uvGridStrip * (gridStripCount - modIndex);
                        return;
                    }
                }
                //上
                lodDelta = lodTrans.w;
                if (lodDelta > 0 && vertexIndex.y == PATCH_MESH_GRID_COUNT)
                {
                    uint gridStripCount = pow(2, lodDelta);
                    uint modIndex = vertexIndex.x % gridStripCount;
                    if (modIndex != 0)
                    {
                        vertex.x += PATCH_MESH_GRID_SIZE * (gridStripCount - modIndex);
                        uv.x += uvGridStrip * (gridStripCount - modIndex);
                        return;
                    }
                }
            }

            //在Node之间留出缝隙供Debug
            float3 ApplyNodeDebug(RenderPatch patch, float3 vertex)
            {
                uint nodeCount = (uint)(5 * pow(2, 5 - patch.lod));
                float nodeSize = _WorldSize.x / nodeCount;
                uint2 nodeLoc = floor((patch.position + _WorldSize.xz * 0.5) / nodeSize);
                float2 nodeCenterPosition = -_WorldSize.xz * 0.5 + (nodeLoc + 0.5) * nodeSize;
                vertex.xz = nodeCenterPosition + (vertex.xz - nodeCenterPosition) * 0.95;
                return vertex;
            }

            uniform float4x4 _HizCameraMatrixVP;

            float3 TransformWorldToUVD(float3 positionWS)
            {
                float4 positionHS = mul(_HizCameraMatrixVP, float4(positionWS, 1.0));
                float3 uvd = positionHS.xyz / positionHS.w;
                uvd.xy = (uvd.xy + 1) * 0.5;
                return uvd;
            }

            void InitializeInputDataCustomT(v2f i, float3 normalTS, out InputData inputData)
            {
                inputData = (InputData)0;
                half sgn = i.tangentWS.w; // should be either +1 or -1
                half3 bitangent = sgn * cross(i.normalWS.xyz, i.tangentWS.xyz);
                half3x3 tangentToWorld = half3x3(i.tangentWS.xyz, bitangent.xyz, i.normalWS.xyz);
                inputData.tangentToWorld = tangentToWorld;
                inputData.normalWS = TransformTangentToWorld(normalTS, tangentToWorld);
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    inputData.shadowCoord = i.shadowCoord;
                #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    inputData.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                #else
                inputData.shadowCoord = half4(0, 0, 0, 0);
                #endif
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
                inputData.fogCoord = i.fogFactorAndVertexLight.x;
                inputData.bakedGI = SAMPLE_GI(i.lightmapUV, i.vertexSH, inputData.normalWS);
                #ifdef _ADDITIONAL_LIGHTS_VERTEX
    inputData.vertexLighting = i.fogFactorAndVertexLight.yzw;
                #endif
            }


            TEXTURE2D(_Control);
            SAMPLER(sampler_Control);
            float4 _Control_ST;
            
            TEXTURE2D(_Splat0) ;
            float4 _Splat0_ST;
            SAMPLER(sampler_Splat0);
            // sampler2D _Splat1;
            // float4 _Splat1_ST;
            // sampler2D _Splat2;
            // float4 _Splat2_ST;
            // sampler2D _Splat3;
            // float4 _Splat3_ST;
            TEXTURE2D(_Normal0);
            float4 _Normal0_ST;
            SAMPLER(sampler_Normal0);
            // sampler2D _Normal1;
            // float4 _Normal1_ST;
            // sampler2D _Normal2;
            // float4 _Normal2_ST;
            // sampler2D _Normal3;
            // float4 _Normal3_ST;
            TEXTURE2D(_NoiseMap) ;
            float4 _NoiseMap_ST;
            SAMPLER(sampler_NoiseMap);
            TEXTURE2D(_MatcapTex);
            float4 _MatcapTex_ST;
            SAMPLER(sampler_MatcapTex);
            //PBR光照
            half4 CustomPBRLight(InputData inputData, BRDFData outBRDFData, Light light, half3 lightDir, half3 normalWS,
                                half3 viewDirWS, half alpha, half smoothness, half matcapvalue, half3 shadowcolor)
            {
                //-----------间接光--------------------
                half3 indirectDiffuse = inputData.bakedGI * outBRDFData.diffuse;
                half3 normalVS = TransformWorldToViewDir(normalWS, true);
                half2 matcapUV = normalVS.xy * 0.5 + 0.5;
                int mip = PerceptualRoughnessToMipmapLevel(outBRDFData.perceptualRoughness);
                half4 matcapTex = SAMPLE_TEXTURECUBE_LOD(_MatcapTex,sampler_MatcapTex, matcapUV, mip);
                half fresnelTerm = Pow4(1.0 - dot(normalWS, viewDirWS));
                half surfaceReduction = 1.0 / (outBRDFData.roughness2 + 1.0);
                half3 F = surfaceReduction * lerp(outBRDFData.specular, outBRDFData.grazingTerm, fresnelTerm);
                half3 indirectSpecular = (matcapTex.rgb * smoothness * matcapvalue) * F;
                //------------------------------------

                //------------直接光照-----------------
                half nl = saturate(dot(lightDir, normalWS)) * (light.distanceAttenuation * light.shadowAttenuation);
                half nv = Pow4(1.0 - dot(normalWS, viewDirWS));
                half3 radiance;
                radiance = nl * light.color;

                half3 halfDir = SafeNormalize(half3(lightDir) + half3(viewDirWS));
                half NoH = saturate(dot(normalWS, halfDir));
                half LoH = saturate(dot(lightDir, halfDir));
                half d = NoH * NoH * outBRDFData.roughness2MinusOne + 1.00001f;

                half LoH2 = LoH * LoH;

                half specularTerm = outBRDFData.roughness2 / ((d * d) * max(0.1h, LoH2) * outBRDFData.
                    normalizationTerm);
                #if defined (SHADER_API_MOBILE) || defined (SHADER_API_SWITCH)
                specularTerm = specularTerm - HALF_MIN;
                specularTerm = clamp(specularTerm, 0.00001, 100.0); // Prevent FP16 overflow on mobiles
                #endif

                half3 diffuseColor = specularTerm * outBRDFData.specular + outBRDFData.diffuse;
                half4 finallColor;
                finallColor.a = alpha;
                finallColor.rgb = diffuseColor * radiance;
                // finallColor.rgb *= light.shadowAttenuation;
                finallColor.rgb += (indirectDiffuse + indirectSpecular);
                return finallColor;
            }

            v2f vert(appdata v)
            {
                v2f o;
                //输入的顶点坐标
                float4 inVertex = v.vertex;
                float2 uv = v.uv;

                RenderPatch patch = PatchList[v.instanceID];
                //一个Node使用8个Patch渲染
                float perPatchUV = patch.perNodeUV / 8.0;
                float2 uvInGlobal = patch.perNodeUV * patch.nodeLocXYAndPatchOffsetZW.xy + perPatchUV * patch.
                    nodeLocXYAndPatchOffsetZW.zw + uv * perPatchUV;
                o.uvglobal = uvInGlobal;
                v.texcoord = uvInGlobal;
                #if ENABLE_LOD_SEAMLESS
                FixLODConnectSeam(inVertex, uv, patch);
                #endif
                uint lod = patch.lod;
                float scale = pow(2, lod);

                uint4 lodTrans = patch.lodTrans;


                inVertex.xz *= scale;
                #if ENABLE_PATCH_DEBUG
                inVertex.xz *= 0.9;
                #endif
                inVertex.xz += patch.position;

                #if ENABLE_NODE_DEBUG
                inVertex.xyz = ApplyNodeDebug(patch, inVertex.xyz);
                #endif

                float2 heightUV = (inVertex.xz + (_WorldSize.xz * 0.5) + 0.5) / (_WorldSize.xz + 1);
                float4 color = SAMPLE_TEXTURE2D(_HeightMap,sampler_HeightMap,heightUV);
                float height = color.r;
                inVertex.y = height * _WorldSize.y;


                float3 normal = SampleNormal(heightUV);
                float3 tangent = ProjectOnPlane((1, 0, 0), normal);
                VertexPositionInputs vertexInput;
                vertexInput.positionWS = inVertex;
                VertexNormalInputs normalInput;
                normalInput.normalWS = normal;
                normalInput.tangentWS = tangent;
                normalInput.bitangentWS = cross(normal, tangent);
                Light light = GetMainLight();
                o.color = max(0.05, dot(light.direction, normal));

                float4 vertex = TransformObjectToHClip(inVertex.xyz);
                vertexInput.positionCS = vertex;
                o.vertex = vertex;

                o.uv = uv * scale * 8;

                #if ENABLE_MIP_DEBUG

                uint4 lodColorIndex = lod + lodTrans;
                o.color *= (debugColorForMip[lodColorIndex.x] +
                    debugColorForMip[lodColorIndex.y] +
                    debugColorForMip[lodColorIndex.z] +
                    debugColorForMip[lodColorIndex.w]) * 0.25;
                #endif
                //BRDF着色计算部分
                half fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
                #if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
                o.positionWS = vertexInput.positionWS;
                #endif
                half3 vertexLight = VertexLighting(vertexInput.positionWS, normalInput.normalWS);
                o.fogFactorAndVertexLight = half4(fogFactor, vertexLight);
                o.tangentWS = float4(tangent, 1);
                o.normalWS = normalInput.normalWS;
                o.viewDirWS = GetCameraPositionWS() - vertexInput.positionWS;

                o.uv.xy = TRANSFORM_TEX(v.texcoord, _Control);
                o.uv1.zw = TRANSFORM_TEX(v.texcoord, _Splat0);
                o.uv1.xy  = o.uv.xy;
                // o.uv2.xy = TRANSFORM_TEX(v.texcoord, _Splat1);
                // o.uv2.zw = TRANSFORM_TEX(v.texcoord, _Splat2);
                // o.uv3.xy = TRANSFORM_TEX(v.texcoord, _Splat3);
                // o.uv3.zw = TRANSFORM_TEX(v.texcoord, _NoiseMap);
                // o.color = half4(TransformWorldToUVD(inVertex.xyz).xy,0,1);
                return o;
            }

            float4 _TerrainColorr1;
            float4 _TerrainColorr2;
            float4 _TerrainColorr3;
            float4 _TerrainColorr4;
            float _NoiseIntensity;
            float4 _NoiseColor;
            float _BumpScaleS0;
            float _BumpScaleS1;
            float _BumpScaleS2;
            float _BumpScaleS3;
            float _SmoothnessS1;
            float _SmoothnessS2;
            float _SmoothnessS3;
            float _SmoothnessS4;
            float4 _ShadowColor;
            float _MatcapValue;

            half4 frag(v2f i) : SV_Target
            {
                // sample the texture
                //half4 col = tex2D(_MainTex, i.uv);
                half4 col = SAMPLE_TEXTURE2D(_MainTex,sampler_MainTex, i.uvglobal);
                col.rgb = (col.rgb + 0.5) / 2.0;
                col.rgb *= i.color;
                half alpha;
                alpha = 1;

                // 采样主贴图--------------------------
                half4 controlMap = SAMPLE_TEXTURE2D(_Control, sampler_Control,i.uv1.xy);
                half4 terrainMap1 = SAMPLE_TEXTURE2D(_Splat0, sampler_Splat0,i.uv1.zw);
                terrainMap1.rgb *= _TerrainColorr1.rgb;
                // half4 terrainMap2 = tex2D(_Splat1, i.uv2.xy);
                // terrainMap2.rgb *= _TerrainColorr2.rgb;
                // half4 terrainMap3 = tex2D(_Splat2, i.uv2.zw);
                // terrainMap3.rgb *= _TerrainColorr3.rgb;
                // half4 terrainMap4 = tex2D(_Splat3, i.uv3.xy);
                //terrainMap4.rgb *= _TerrainColorr4.rgb;
                col.rgb += terrainMap1.rgb * controlMap.r;
                // col.rgb += terrainMap2.rgb * controlMap.g;
                // col.rgb += terrainMap3.rgb * controlMap.b;
                // col.rgb += terrainMap4.rgb * controlMap.a;
                col.rgb /= (controlMap.r + controlMap.g + controlMap.b + controlMap.a);

                half4 noise = SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap,i.uv3.zw);
                col.rgb = lerp(col.rgb, col.rgb * noise.r, _NoiseIntensity);
                col.rgb = lerp(col.rgb * _NoiseColor, col.rgb, noise.r);

                half3 normalTerrain = 0;
                normalTerrain += controlMap.r * UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal0,sampler_Normal0, i.uv1.zw), _BumpScaleS0);
                // normalTerrain += controlMap.g * UnpackNormalScale(tex2D(_Normal1, i.uv2.xy), _BumpScaleS1);
                // normalTerrain += controlMap.b * UnpackNormalScale(tex2D(_Normal2, i.uv2.zw), _BumpScaleS2);
                // normalTerrain += controlMap.a * UnpackNormalScale(tex2D(_Normal3, i.uv3.xy), _BumpScaleS3);
                normalTerrain = normalize(normalTerrain);

                half smoothnessTerrain = 0;
                smoothnessTerrain += controlMap.r * terrainMap1.a * _SmoothnessS1;
                // smoothnessTerrain += controlMap.g * terrainMap2.a * _SmoothnessS2;
                // smoothnessTerrain += controlMap.b * terrainMap3.a * _SmoothnessS3;
                // smoothnessTerrain += controlMap.a * terrainMap4.a * _SmoothnessS4;
                half smoothness = smoothnessTerrain;

                InputData inputData;
                InitializeInputDataCustomT(i, normalTerrain, inputData);


                BRDFData outBRDFData;
                //outBRDFData.diffuse = col.rgb * oneMinusReflectivityMetallic;
                outBRDFData.diffuse = col.rgb;
                outBRDFData.specular = lerp(kDieletricSpec.rgb, col.rgb, 0);
                outBRDFData.grazingTerm = saturate(smoothness);
                outBRDFData.perceptualRoughness = PerceptualSmoothnessToPerceptualRoughness(smoothness);
                outBRDFData.roughness = max(PerceptualRoughnessToRoughness(outBRDFData.perceptualRoughness), HALF_MIN);
                outBRDFData.roughness2 = max(outBRDFData.roughness * outBRDFData.roughness, HALF_MIN);
                outBRDFData.normalizationTerm = outBRDFData.roughness * half(4.0) + half(2.0);
                outBRDFData.roughness2MinusOne = outBRDFData.roughness2 - half(1.0);

                half4 shadowMask = CalculateShadowMask(inputData);

                Light light = GetMainLight(inputData.shadowCoord, i.positionWS, shadowMask);
                half3 lihgtDir = light.direction;
                half3 viewDirWS = inputData.viewDirectionWS;
                half3 normalWS = NormalizeNormalPerPixel(inputData.normalWS);

                #ifdef _DBUFFER
                    ApplyDecalToBaseColor(i.positionCS, outBRDFData.diffuse);
                #endif
                half4 finallColor = 0;
                finallColor = CustomPBRLight(inputData, outBRDFData, light, lihgtDir, normalWS, viewDirWS, alpha,
                                              smoothness, _MatcapValue, _ShadowColor.rgb);

                #if defined (_ADDITIONAL_LIGHTS)
                    int pixelLightCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(pixelLightCount)
                    for(int index = 0; index < pixelLightCount; index ++)
                        {
                             Light addlight = GetAdditionalLight(index, i.positionWS, shadowMask);
                            //half3 addLightColor = CustomPBRLight(inputData, outBRDFData, addlight, addlight.direction, normalWS, viewDirWS, alpha, smoothness, _MatcapValue, _ShadowColor.rgb);
                            half3 addLightColor = saturate(dot(addlight.direction, normalWS)) * addlight.color * outBRDFData.diffuse;
                            finallColor.rgb += addLightColor * addlight.distanceAttenuation * addlight.shadowAttenuation;
                        }
                     LIGHT_LOOP_END
                #endif
                finallColor.rgb = MixFog(finallColor.rgb, inputData.fogCoord);
                return half4(finallColor.rgb,alpha);
            }
            ENDHLSL
        }
    }
}