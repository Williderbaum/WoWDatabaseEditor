﻿using System.Collections;
using TheAvaloniaOpenGL.Resources;
using TheEngine;
using TheEngine.Coroutines;
using TheEngine.Data;
using TheEngine.Entities;
using TheEngine.Handles;
using TheEngine.Interfaces;
using TheMaths;
using WDE.MpqReader.DBC;
using WDE.MpqReader.Readers;
using WDE.MpqReader.Structures;
using IInputManager = TheEngine.Interfaces.IInputManager;

// ReSharper disable InconsistentNaming

namespace WDE.MapRenderer.Managers
{
    enum ModelVertexShader
    {
        Diffuse_T1,
        Diffuse_T2,
        Diffuse_Env,
        Diffuse_T1_T2,
        Diffuse_T1_Env,
        Diffuse_Env_T2,
        Diffuse_Env_Env,
    };
    
    enum ModelPixelShader
    {
        Opaque = 0,
        Opaque_Opaque,
        Opaque_Mod,
        Opaque_Mod2x,
        Opaque_Mod2xNA,
        Opaque_Add,
        Opaque_AddNA,
        Opaque_AddAlpha,
        Opaque_AddAlpha_Alpha,
        Opaque_Mod2xNA_Alpha,
        Mod,
        Mod_Opaque,
        Mod_Mod,
        Mod_Mod2x,
        Mod_Mod2xNA,
        Mod_Add,
        Mod_AddNA,
        Mod2x,
        Mod2x_Mod,
        Mod2x_Mod2x,
        Add,
        Add_Mod,
        Fade,
        Decal
    };

    
    public class MdxManager : System.IDisposable
    {
        public class MdxInstance
        {
            public IMesh mesh;
            public Material[] materials;
            public M2 model;
            public float scale = 1;
            private bool? hasAnimations;
            public List<(M2AttachmentType, MdxInstance)>? attachments;

            public bool HasAnimations
            {
                get
                {
                    if (hasAnimations.HasValue)
                        return hasAnimations.Value;
                    
                    if (model.sequences.Length == 0)
                        return false;

                    if (model.bones.Length == 0)
                        return false;

                    model.bones.LoadAnimation(0);

                    bool anyHas = false;
                    for (int boneIndex = 0; boneIndex < model.bones.Length; ++boneIndex)
                    {
                        if (model.bones[boneIndex].translation.Length > 0 &&
                            model.bones[boneIndex].translation.Timestamps(0).Length > 0)
                        {
                            anyHas  = true;
                            break;
                        }
                        if (model.bones[boneIndex].rotation.Length > 0 &&
                            model.bones[boneIndex].rotation.Timestamps(0).Length > 0)
                        {
                            anyHas  = true;
                            break;
                        }
                        if (model.bones[boneIndex].scale.Length > 0 &&
                            model.bones[boneIndex].scale.Timestamps(0).Length > 0)
                        {
                            anyHas  = true;
                            break;
                        }
                    }

                    hasAnimations = anyHas;
                    return  anyHas;
                }
            }

            public void Dispose(IMeshManager meshManager)
            {
                meshManager.DisposeMesh(mesh);
            }
        }

        private Dictionary<string, MdxInstance?> meshes = new();
        private Dictionary<string, Task<MdxInstance?>> meshesCurrentlyLoaded = new();
        private Dictionary<uint, MdxInstance?> creaturemeshes = new();
        private Dictionary<uint, Task<MdxInstance?>> gameObjectMeshesCurrentlyLoaded = new();
        private Dictionary<uint, MdxInstance?> gameObjectmeshes = new();
        private Dictionary<uint, Task<MdxInstance?>> creatureMeshesCurrentlyLoaded = new();
        private Dictionary<(uint displayId, bool right, ushort raceGender), MdxInstance?> itemMeshes = new();
        private Dictionary<(uint displayId, bool right, ushort raceGender), Task<MdxInstance?>> itemMeshesCurrentlyLoaded = new();
        private readonly IGameFiles gameFiles;
        private readonly IMeshManager meshManager;
        private readonly IMaterialManager materialManager;
        private readonly WoWTextureManager textureManager;
        private readonly CreatureDisplayInfoStore creatureDisplayInfoStore;
        private readonly CreatureDisplayInfoExtraStore creatureDisplayInfoExtraStore;
        private readonly ItemDisplayInfoStore itemDisplayInfoStore;
        private readonly CharHairGeosetsStore charHairGeosetsStore;
        private readonly CharacterFacialHairStylesStore characterFacialHairStylesStore;
        private readonly CharSectionsStore charSectionsStore;
        private readonly HelmetGeosetVisDataStore helmetGeosetVisDataStore;
        private readonly CreatureModelDataStore creatureModelDataStore;
        private readonly GameObjectDisplayInfoStore gameObjectDisplayInfoStore;
        private readonly ChrRacesStore racesStore;
        private readonly Engine engine;
        private readonly IGameContext gameContext;
        private readonly IInputManager inputManager;
        private readonly IUIManager uiManager;

        private NativeBuffer<Matrix> identityBonesBuffer;

        public MdxManager(IGameFiles gameFiles, 
            IMeshManager meshManager, 
            IMaterialManager materialManager,
            WoWTextureManager textureManager,
            CreatureDisplayInfoStore creatureDisplayInfoStore,
            CreatureDisplayInfoExtraStore creatureDisplayInfoExtraStore,
            ItemDisplayInfoStore itemDisplayInfoStore,
            CharHairGeosetsStore charHairGeosetsStore,
            CharacterFacialHairStylesStore characterFacialHairStylesStore,
            CharSectionsStore charSectionsStore,
            HelmetGeosetVisDataStore helmetGeosetVisDataStore,
            CreatureModelDataStore creatureModelDataStore,
            GameObjectDisplayInfoStore gameObjectDisplayInfoStore,
            ChrRacesStore racesStore,
            Engine engine,
            IGameContext gameContext,
            IInputManager inputManager,
            IUIManager uiManager)
        {
            this.gameFiles = gameFiles;
            this.meshManager = meshManager;
            this.materialManager = materialManager;
            this.textureManager = textureManager;
            this.creatureDisplayInfoStore = creatureDisplayInfoStore;
            this.creatureDisplayInfoExtraStore = creatureDisplayInfoExtraStore;
            this.itemDisplayInfoStore = itemDisplayInfoStore;
            this.charHairGeosetsStore = charHairGeosetsStore;
            this.characterFacialHairStylesStore = characterFacialHairStylesStore;
            this.charSectionsStore = charSectionsStore;
            this.helmetGeosetVisDataStore = helmetGeosetVisDataStore;
            this.creatureModelDataStore = creatureModelDataStore;
            this.gameObjectDisplayInfoStore = gameObjectDisplayInfoStore;
            this.racesStore = racesStore;
            this.engine = engine;
            this.gameContext = gameContext;
            this.inputManager = inputManager;
            this.uiManager = uiManager;
            identityBonesBuffer = engine.CreateBuffer<Matrix>(BufferTypeEnum.StructuredBufferVertexOnly, AnimationSystem.MAX_BONES, BufferInternalFormat.Float4);
            identityBonesBuffer.UpdateBuffer(AnimationSystem.IdentityBones(AnimationSystem.MAX_BONES).Span);
        }

        private static float UintAsFloat(uint i)
        {
            unsafe
            {
                uint* iRef = &i;
                return *((float*)iRef);
            }
        }

        // Sources : wowdev.wiki/DB/ItemDisplayInfo#Geoset_Group_Field_Meaning and wowdev.wiki/Character_Customization#Geosets
        private readonly int geosetSkin = 0;
        private readonly int geosetHair = 1;
        private readonly int geosetFacial1 = 101;
        private readonly int geosetFacial2 = 201;
        private readonly int geosetFacial3 = 301;
        private readonly int geosetGlove = 401;  // {0: No Geoset; 1: Default; 2: Thin; 3: Folded; 4: Thick}
        private readonly int geosetBoots = 501; // {0: No Geoset; 1: Default; 2: High Boot; 3: Folded Boot; 4: Puffed; 5: Boot 4}
        private readonly int geosetTail = 600; 
        private readonly int geosetEars = 700;
        private readonly int geosetSleeves = 801; // {1: Default (No Geoset); 2: Flared Sleeve; 3: Puffy Sleeve; 4: Panda Collar Shirt}
        private readonly int geosetlegcuffs = 901; // {1: Default (No Geoset); 2: Flared Pant Cuff; 3: Knickers; 4: Panda Pants}
        private readonly int geosetChest = 1001; // {1: Default (No Geoset); 2: Doublet; 3: Body 2; 4: Body 3}
        private readonly int geosetpants = 1101; // {1: Default (No Geoset); 2: Mini Skirt; 4: Heavy}
        private readonly int geosetTabard = 1201; // {1: Default (No Geoset); 2: Tabard}
        private readonly int geosetTrousers = 1301; // {0: No Geoset; 1: Default; 2: Long Skirt}
        private readonly int geosetFemaleLoincloth = 1400; // DH/Pandaren female Loincloth
        private readonly int geosetCloak = 1501; // {1: Default (No Geoset); 2: Ankle Length; 3: Knee Length; 4: Split Banner; 5: Tapered Waist;
                                // 6: Notched Back; 7: Guild Cloak; 8: Split (Long); 9: Tapered (Long); 10: Notched (Long)}
        private readonly int geosetNoseEarrings = 1600; // geoset4 in CharacterFacialHairStyles ?
        private readonly int geosetEyeglows = 1700; // small\big ears for BloodElves
        private readonly int geosetBelt = 1801; // {0: No Geoset; 1: Default; 2: Heavy Belt; 3: Panda Cord Belt}
        private readonly int geosetBone = 1900; // ?
        private readonly int geosetFeet = 2001; // {0: No Geoset; 1: Default (Basic Shoes); 2: Toes}
        private readonly int geosetHead = 2101; // {0: No Geoset; 1: Show Head}. default = show head ?
        private readonly int geosetTorso = 2201;// {0: No Geoset; 1: Default; 2: Covered Torso}
        private readonly int geosetHandsAttachments = 2301;  // {0: No Geoset; 1: Default}
        private readonly int geosetHeadAttachments = 2400;
        private readonly int geosetBlindfolds = 2500;
        private readonly int geosetShoulders = 2600; // {0: No Geoset; 1: Show Shoulders, SL+ 2: Non-Mythic only; 3: Mythic}
        private readonly int geosetHelm = 2701; // {1: Default (No Geoset); 2: Helm 1; SL+ 3: Non-Mythic only; 4: Mythic}
        private readonly int geosetUNK28 = 2801; // {0: No Geoset; 1: Default}
        // BFA/SL+ geosets.

        public IEnumerator LoadCreatureModel(uint displayid, TaskCompletionSource<MdxInstance?> result)
        {
            if (creaturemeshes.TryGetValue(displayid, out var loadedMeshInstance))
            {
                result.SetResult(loadedMeshInstance);
                yield break;
            }
            if (creatureMeshesCurrentlyLoaded.TryGetValue(displayid, out var loadInProgress))
            {
                yield return new WaitForTask(loadInProgress);
                result.SetResult(creaturemeshes[displayid]);
                yield break;
            }
            
            var completion = new TaskCompletionSource<MdxInstance?>();
            creatureMeshesCurrentlyLoaded[displayid] = completion.Task;

            if (!creatureDisplayInfoStore.TryGetValue(displayid, out var creatureDisplayInfo) ||
                !creatureModelDataStore.TryGetValue((uint)creatureDisplayInfo.ModelId, out var modelData))
            {
                Console.WriteLine("Cannot find model " + displayid);
                creaturemeshes[displayid] = null;
                completion.SetResult(null);
                creatureMeshesCurrentlyLoaded.Remove(displayid);
                result.SetResult(null);
                yield break;
            }
            
            var m2FilePath = modelData.ModelName.Replace("mdx", "M2", StringComparison.InvariantCultureIgnoreCase);
            m2FilePath = m2FilePath.Replace("mdl", "M2", StringComparison.InvariantCultureIgnoreCase); // apaprently there are still some MDL models

            var skinFilePath = m2FilePath.Replace(".m2", "00.skin", StringComparison.InvariantCultureIgnoreCase);
            var file = gameFiles.ReadFile(m2FilePath);

            yield return new WaitForTask(file);

            var skinFile = gameFiles.ReadFile(skinFilePath);
            
            yield return new WaitForTask(skinFile);

            if (file.Result == null || skinFile.Result == null)
            {
                Console.WriteLine("Cannot find model " + displayid);
                creaturemeshes[displayid] = null;
                completion.SetResult(null);
                creatureMeshesCurrentlyLoaded.Remove(displayid);
                result.SetResult(null);
                yield break;
            }
            
            M2 m2 = null!;
            M2Skin skin = new();
            Vector3[] vertices = null!;
            Vector3[] normals = null!;
            Vector2[] uv1 = null!;
            Vector2[] uv2 = null!;
            Color[] boneWeights = null!;
            Color[] boneIndices = null!;
            
            yield return new WaitForTask(Task.Run(() =>
            {
                m2 = M2.Read(new MemoryBinaryReader(file.Result), m2FilePath, p =>
                {
                    // TODO: can I use ReadFileSync? Can be problematic...
                    var bytes = gameFiles.ReadFileSyncLocked(p, true);
                    if (bytes == null)
                        return null;
                    return new MemoryBinaryReader(bytes);
                });
                file.Result.Dispose();
                skin = new M2Skin(new MemoryBinaryReader(skinFile.Result));
                skinFile.Result.Dispose();
                var count = m2.vertices.Length;
                vertices = new Vector3[count];
                normals = new Vector3[count];
                uv1 = new Vector2[count];
                uv2 = new Vector2[count];
                boneWeights = new Color[count];
                boneIndices = new Color[count];

                for (int i = 0; i < count; ++i)
                {
                    ref readonly var vert = ref m2.vertices[skin.Vertices[i]];
                    vertices[i] = vert.pos;
                    normals[i] = vert.normal;
                    uv1[i] = vert.tex_coord1;
                    uv2[i] = vert.tex_coord2;
                    boneWeights[i] = vert.bone_weights;
                    boneIndices[i] = vert.bone_indices;
                }
            }));
            
            var md = new MeshData(vertices, normals, uv1, new ushort[] { }, null, null, uv2, boneWeights, boneIndices);
            var mesh = meshManager.CreateMesh(md);
            mesh.SetSubmeshCount(skin.Batches.Length);
            // 1 : load items to define active geosets
            // 2 : if no item, set default geosets
            bool isCharacterModel = false;
            HashSet<int>? activeGeosets = null;
            
            List<(M2AttachmentType, MdxInstance)>? attachments = null;
            
            if (creatureDisplayInfo.ExtendedDisplayInfoID > 0)
            {
                if (creatureDisplayInfoExtraStore.TryGetValue(creatureDisplayInfo.ExtendedDisplayInfoID, out var displayinfoextra))
                {
                    attachments = new();
                    var itemModelPromise = new TaskCompletionSource<MdxInstance?>();

                    isCharacterModel = true;
                    int geosetSkin = 0;
                    int geosetHair = 0; // Hair: { 1 - 21: various hairstyles}
                    int geosetFacial1 = 100; // Facial1: {1-8: varies} (usually beard, but not always)
                    int geosetFacial2 = 200; // Facial2: {1: none (DNE), 2-6: varies} (usually mustacheᵘ, but not always)
                    int geosetFacial3 = 300; // Facial3: { 1: none(DNE), 2 - 11: varies} (usually sideburnsᵘ, but not always)
                    int geosetGlove = 401;  // {0: No Geoset; 1: Default; 2: Thin; 3: Folded; 4: Thick}
                    int geosetBoots = 501; // {0: No Geoset; 1: Default; 2: High Boot; 3: Folded Boot; 4: Puffed; 5: Boot 4}
                    int geosetTail = 600; 
                    int geosetEars = 702; // {1: none (DNE), 2: ears}
                    int geosetSleeves = 801; // {1: Default (No Geoset); 2: Flared Sleeve; 3: Puffy Sleeve; 4: Panda Collar Shirt}
                    int geosetlegcuffs = 901; // {1: Default (No Geoset); 2: Flared Pant Cuff; 3: Knickers; 4: Panda Pants}
                    int geosetChest = 1001; // {1: Default (No Geoset); 2: Doublet; 3: Body 2; 4: Body 3}
                    int geosetpants = 1101; // {1: Default (No Geoset); 2: Mini Skirt; 4: Heavy}
                    int geosetTabard = 1201; // {1: Default (No Geoset); 2: Tabard}
                    int geosetTrousers = 1301; // {0: No Geoset; 1: Default; 2: Long Skirt}
                    int geosetFemaleLoincloth = 1400; // DH/Pandaren female Loincloth
                    int geosetCloak = 1501; // {1: Default (No Geoset); 2: Ankle Length; 3: Knee Length; 4: Split Banner; 5: Tapered Waist; // 6: Notched Back; 7: Guild Cloak; 8: Split (Long); 9: Tapered (Long); 10: Notched (Long)}
                    int geosetNoseEarrings = 1600; // geoset4 in CharacterFacialHairStyles ?
                    int geosetEyeglows = 1700; // small\big ears for BloodElves
                    int geosetBelt = 1801; // {0: No Geoset; 1: Default; 2: Heavy Belt; 3: Panda Cord Belt}
                    int geosetBone = 1900; // ?
                    int geosetFeet = 2001; // {0: No Geoset; 1: Default (Basic Shoes); 2: Toes}
                    int geosetHead = 2101; // {0: No Geoset; 1: Show Head}. default = show head ?
                    int geosetTorso = 2201;// {0: No Geoset; 1: Default; 2: Covered Torso}
                    int geosetHandsAttachments = 2301;  // {0: No Geoset; 1: Default}
                    int geosetHeadAttachments = 2400;
                    int geosetBlindfolds = 2500;
                    int geosetShoulders = 2600; // {0: No Geoset; 1: Show Shoulders, SL+ 2: Non-Mythic only; 3: Mythic}
                    int geosetHelm = 2701; // {1: Default (No Geoset); 2: Helm 1; SL+ 3: Non-Mythic only; 4: Mythic}
                    int geosetUNK28 = 2801; // {0: No Geoset; 1: Default}

                    // hair
                    CharHairGeosets hairstyle = charHairGeosetsStore.FirstOrDefault(x => x.RaceID == displayinfoextra.Race && x.SexId == displayinfoextra.Gender && x.VariationId == displayinfoextra.HairStyle); // maybe +1 like beards ?
                    if (hairstyle != null)
                    {
                        geosetHair += hairstyle.GeosetId;
                        // use CharHairGeosetsStore.ShowScalp (bald) or is it only some client stuff ?
                        // showscalp seems to be used for some races like goblins that don't use normal variations, but how ?
                    }
                    //else Console.WriteLine("invalid hairstyle id for display id " + creatureDisplayInfo.Id + " race " + displayinfoextra.Race + " gender " + displayinfoextra.Gender);
                    // goblin males require to always lookup hairstyle even with default because their scalp geoset is an edditional geoset defined in charHairGeosetsStore variation 0
                    // but goblin females don't have an entry in it at all and will error there


                    // facial hair
                    // using first of default because it seems some NPCs use invalid variation id
                    CharacterFacialHairStyles facialhairstyle = characterFacialHairStylesStore.FirstOrDefault(x => x.RaceID == displayinfoextra.Race && x.SexId == displayinfoextra.Gender && x.VariationId == displayinfoextra.BeardStyle); // maybe variation +1
                    if (facialhairstyle != null)
                    {
                        geosetFacial1 += facialhairstyle.Geoset1;
                        geosetFacial2 += facialhairstyle.Geoset2; // apparently this is group 3 ? verify in game.
                        geosetFacial3 += facialhairstyle.Geoset3;
                        geosetNoseEarrings += facialhairstyle.Geoset4;
                        geosetEyeglows += facialhairstyle.Geoset5;
                    }
                    // else Console.WriteLine("invalid facialhairstyle id for display id " + creatureDisplayInfo.Id + " race " + displayinfoextra.Race + " gender " + displayinfoextra.Gender);

                    if (displayinfoextra.Helm > 0)
                    {
                        ItemDisplayInfo helmDisplayInfo = itemDisplayInfoStore[displayinfoextra.Helm];
                        // geoset group 2 ? some enable/disable 2100 (head) ?
                        geosetHelm = 2702 + helmDisplayInfo.geosetGroup1;
                        yield return LoadItemMesh(displayinfoextra.Helm, false, displayinfoextra.Race, displayinfoextra.Gender, itemModelPromise);
                        if (itemModelPromise.Task.Result != null)
                        {
                            attachments.Add((M2AttachmentType.Helm, itemModelPromise.Task.Result));

                            // set helm geoset visibility
                            // todo : figure out what negative hidegeoset numbers means
                            // do we restore default or set none ?
                            int helmetGeosetVisDataId = 0;

                            if (displayinfoextra.Gender == 1) // female
                                    helmetGeosetVisDataId = helmDisplayInfo.helmetGeosetVisFemale;
                            else // male
                                    helmetGeosetVisDataId = helmDisplayInfo.helmetGeosetVisMale;

                            if (helmetGeosetVisDataId > 0)
                            {
                                HelmetGeosetVisData helmetGeosetVisData = helmetGeosetVisDataStore[(uint)helmetGeosetVisDataId];
                                for (int i = 0; i < 32; i++)
                                {
                                    uint maskPow = (uint)Math.Pow(2, i-1);
                                    // if the current set hair geoset is this flag
                                    if (geosetHair == i)
                                    { // check if this flag is set to be hidden in helmetGeosetVisData
                                        if ((helmetGeosetVisData.HairFlags & maskPow) != 0)
                                            geosetHair = 1;
                                    }
                                    if ((geosetEars - 700) == i) // ears
                                    {
                                        if ((helmetGeosetVisData.EarsFlags & maskPow) != 0)
                                            geosetEars = 701; // no ears. maybe 700(don't render at all)
                                    }
                                    if ((geosetFacial1 - 100) == i) // facial1
                                        if ((helmetGeosetVisData.Facial1Flags & maskPow) != 0)
                                            geosetFacial1 = 101;
                                    if ((geosetFacial2 - 200) == i) // facial2
                                    {
                                        if ((helmetGeosetVisData.Facial2Flags & maskPow) != 0)
                                            geosetFacial2 = 201;
                                    }
                                    if ((geosetFacial3 - 300) == i) // facial3
                                    {
                                        if ((helmetGeosetVisData.Facial3Flags & maskPow) != 0)
                                            geosetFacial3 = 301;
                                    }
                                    if ((geosetEyeglows - 1700) == i) // eyes
                                    {
                                        if ((helmetGeosetVisData.EyesFlags & maskPow) != 0)
                                            geosetEyeglows = 1700;
                                    }
                                    // guessed MiscFlags is geosetNoseEarrings, could be wrong, no documentation available.
                                    if ((geosetNoseEarrings - 1600) == i) // eyes
                                    {
                                        if ((helmetGeosetVisData.MiscFlags & maskPow) != 0)
                                            geosetNoseEarrings = 1600;
                                    }
                                }
                            }
                        }
                        itemModelPromise = new();
                    }

                    if (displayinfoextra.Shoulder > 0)
                    {
                        geosetShoulders = 2601 + itemDisplayInfoStore[(uint)displayinfoextra.Shoulder].geosetGroup1;
                        yield return LoadItemMesh(displayinfoextra.Shoulder, false, 0, 0, itemModelPromise);
                        if (itemModelPromise.Task.Result != null)
                            attachments.Add((M2AttachmentType.ShoulderLeft, itemModelPromise.Task.Result));
                        itemModelPromise = new();
                        
                        yield return LoadItemMesh(displayinfoextra.Shoulder, true, 0, 0, itemModelPromise);
                        if (itemModelPromise.Task.Result != null)
                            attachments.Add((M2AttachmentType.ShoulderRight, itemModelPromise.Task.Result));
                        itemModelPromise = new();
                    }

                    if (displayinfoextra.Shirt > 0)
                    {
                        geosetSleeves = 801 + itemDisplayInfoStore[(uint)displayinfoextra.Shirt].geosetGroup1;
                        geosetChest = 1001 + itemDisplayInfoStore[(uint)displayinfoextra.Shirt].geosetGroup2;
                    }
                    if (displayinfoextra.Cuirass > 0)
                    {
                        geosetSleeves = 801 + itemDisplayInfoStore[(uint)displayinfoextra.Cuirass].geosetGroup1;
                        geosetChest = 1001 + itemDisplayInfoStore[(uint)displayinfoextra.Cuirass].geosetGroup2;
                        // 1301 trousers set below
                        // in later expensions, geoset group 4 and 5 ?
                    }

                    if (displayinfoextra.Legs > 0)
                    {
                        geosetpants = 801 + itemDisplayInfoStore[(uint)displayinfoextra.Legs].geosetGroup1;
                        geosetlegcuffs = 1001 + itemDisplayInfoStore[(uint)displayinfoextra.Legs].geosetGroup2;
                        geosetTrousers = 1301 + itemDisplayInfoStore[(uint)displayinfoextra.Legs].geosetGroup3;

                        // Chest Robes set legs trousers in priority
                        // Priority : Chest geosetGroup[2] (1301 set) > Pants geosetGroup[2] (1301 set)
                        if (displayinfoextra.Cuirass > 0)
                            if (itemDisplayInfoStore[(uint)displayinfoextra.Cuirass].geosetGroup3 > 0)
                                geosetTrousers = 1301 + itemDisplayInfoStore[(uint)displayinfoextra.Cuirass].geosetGroup3;

                    }

                    if (displayinfoextra.Boots > 0)
                    {
                        geosetBoots = 501 + itemDisplayInfoStore[(uint)displayinfoextra.Boots].geosetGroup1;
                        geosetFeet = 2002 + itemDisplayInfoStore[(uint)displayinfoextra.Boots].geosetGroup2;
                    }

                    if (displayinfoextra.Gloves > 0)
                    {
                        geosetGlove = 401 + itemDisplayInfoStore[(uint)displayinfoextra.Gloves].geosetGroup1;
                        geosetHandsAttachments = 2301 + itemDisplayInfoStore[(uint)displayinfoextra.Gloves].geosetGroup2;
                    }

                    if (displayinfoextra.Cape > 0)
                    {
                        geosetCloak = 1501 + itemDisplayInfoStore[(uint)displayinfoextra.Cape].geosetGroup1;
                    }

                    if (displayinfoextra.Tabard > 0)
                    {
                        geosetTabard = 1201 + itemDisplayInfoStore[(uint)displayinfoextra.Tabard].geosetGroup1;
                    }

                    if (displayinfoextra.Belt > 0) // priority : belt > tabard
                    {
                        geosetBelt = 1801 + itemDisplayInfoStore[(uint)displayinfoextra.Belt].geosetGroup1;
                    }
                    // Priority : Chest geosetGroup[2] (1301 set) > Pants geosetGroup[2] (1301 set) > Boots geosetGroup[0] (501 set) > Pants geosetGroup[1] (901 set)
                    
                    
                    activeGeosets = new HashSet<int>(){ geosetSkin, geosetHair, geosetFacial1, geosetFacial2, geosetFacial3, geosetGlove, geosetBoots, geosetTail, geosetEars,
                        geosetSleeves, geosetlegcuffs, geosetChest, geosetpants, geosetTabard, geosetTrousers, geosetFemaleLoincloth, geosetCloak, geosetNoseEarrings, geosetEyeglows, geosetBelt, geosetBone, geosetFeet,
                        geosetHead, geosetTorso, geosetHandsAttachments, geosetHeadAttachments, geosetBlindfolds, geosetShoulders, geosetHelm, geosetUNK28 };
                }
            }
            
            Material[] materials = new Material[skin.Batches.Length];
            int j = 0;
            foreach (var batch in skin.Batches)
            {
                if (batch.skinSectionIndex == ushort.MaxValue ||
                    batch.materialIndex >= m2.materials.Length ||
                    batch.skinSectionIndex >= skin.SubMeshes.Length)
                {
                    Console.WriteLine("Sth wrong with batch " + j + " in model " + displayid);
                    continue;
                }

                var sectionSkinSectionId = skin.SubMeshes[batch.skinSectionIndex].skinSectionId;
                var sectionIndexCount = skin.SubMeshes[batch.skinSectionIndex].indexCount;
                var sectionIndexStart = skin.SubMeshes[batch.skinSectionIndex].indexStart;

                // titi, check if element is active
                // loading all meshes for non humanoids (crdisplayinfoextra users), might need tob e tweaked
                if (isCharacterModel && !activeGeosets!.Contains(sectionSkinSectionId))
                    continue;

                mesh.SetIndices(skin.Indices.AsSpan(sectionIndexStart, Math.Min(sectionIndexCount, skin.Indices.Length - sectionIndexStart)), j++);

                TextureHandle? th = null;
                TextureHandle? th2 = null;
                for (int i = 0; i < (batch.textureCount >= 5 ? 1 : batch.textureCount); ++i)
                {
                    if (batch.textureLookupId + i >= m2.textureLookupTable.Length)
                    {
                        if (th.HasValue)
                            th2 = textureManager.EmptyTexture;
                        else
                            th = textureManager.EmptyTexture;
                        Console.WriteLine("File " + displayid + " batch " + j + " tex " + i + " out of range");
                        continue;
                    }
                    var texId = m2.textureLookupTable[batch.textureLookupId + i];
                    if (texId == -1)
                    {
                        if (th.HasValue)
                            th2 = textureManager.EmptyTexture;
                        else
                            th = textureManager.EmptyTexture;
                    }
                    else
                    {
                        var textureDefType = m2.textures[texId].type;
                        
                        string? texFile = null;
                        
                        if (textureDefType == 0) // if texture is hardcoded
                            texFile = m2.textures[texId].filename.AsString();

                        // character models
                        else if (isCharacterModel) // doesn't have a CreatureDisplayInfoExtra entry
                        {
                            CreatureDisplayInfoExtra displayinfoextra = creatureDisplayInfoExtraStore[
                                creatureDisplayInfoStore[displayid].ExtendedDisplayInfoID];

                            if (textureDefType == M2Texture.TextureType.TEX_COMPONENT_SKIN) // character skin
                            {
                                // This is for player characters... Creatures always come with a baked texture.
                                // texFile = charSectionsStore.First(x => x.RaceID == displayinfoextra.Race
                                // && x.BaseSection == 0 && x.ColorIndex == displayinfoextra.SkinColor).TextureName1;
                                texFile = "textures\\BakedNpcTextures\\" + displayinfoextra.Texture;
                            }

                            // only seen for tauren female facial features
                            else if (textureDefType == M2Texture.TextureType.TEX_COMPONENT_SKIN_EXTRA)
                            {
                                // Console.WriteLine("skin extra extdisp id : " + displayinfoextra.Id);
                                // use skin color or hair color ?
                                texFile = charSectionsStore.FirstOrDefault(x => x.RaceID == displayinfoextra.Race && x.SexId == displayinfoextra.Gender
                                && x.ColorIndex == displayinfoextra.SkinColor && x.BaseSection == 0)?.TextureName2;
                            }

                            // mostly used for cloaks
                            else if (textureDefType == M2Texture.TextureType.TEX_COMPONENT_OBJECT_SKIN)
                            {
                                // cloak
                                if (1500 <= sectionSkinSectionId && sectionSkinSectionId <= 1599)
                                {
                                    var capedisplayinfo = itemDisplayInfoStore.First(x => x.Id == displayinfoextra.Cape);
                                    texFile = "Item\\ObjectComponents\\Cape\\" + capedisplayinfo.LeftModelTexture + ".blp";
                                    if (string.IsNullOrEmpty(texFile))
                                        Console.WriteLine("Couldn't get Cape texture from displayextra : " + displayinfoextra);
                                }
                                else
                                {
                                    // there are probably other object skin types than capes
                                    Console.WriteLine("geoset group not implemented for TEX_COMPONENT_SKIN_EXTRA : " + sectionSkinSectionId);
                                }
                            }

                            else if (textureDefType == M2Texture.TextureType.TEX_COMPONENT_CHAR_HAIR || textureDefType == M2Texture.TextureType.TEX_COMPONENT_CHAR_FACIAL_HAIR) // character skin
                            {
                                // CharHairTextures or charsection ?
                                // 1st version, it's awful.
                                // string hairid = displayinfoextra.HairColor.ToString();
                                // 
                                // if (displayinfoextra.HairColor < 10)
                                //     hairid = "0" + hairid;
                                // 
                                // var pathsplit = m2FilePath.Split('\\');
                                // texFile = pathsplit[0] + "\\" + pathsplit[1] + "\\Hair00_" + hairid + ".blp";

                                
                                if (sectionSkinSectionId >= 100) // facial hair :: 100-400
                                {
                                    // https://wowdev.wiki/DB/CharSections#Field_Descriptions

                                    // need to combine lower and upper ?
                                    // issue : many of the textures referenced in charSections don't actually exist
                                    // texFile = charSectionsStore.First(x => x.RaceID == displayinfoextra.Race && x.SexId == displayinfoextra.Gender
                                    // && x.ColorIndex == displayinfoextra.HairColor && x.VariationIndex == displayinfoextra.BeardStyle && x.BaseSection == 2).TextureName2;

                                    // this makes no sense but it works, use the hair section but use beard style to get the variation
                                    texFile = charSectionsStore.FirstOrDefault(x => x.RaceID == displayinfoextra.Race && x.SexId == displayinfoextra.Gender
                                    && x.ColorIndex == displayinfoextra.HairColor && x.VariationIndex == displayinfoextra.BeardStyle && x.BaseSection == 3)?.TextureName1;

                                }
                                else // hair < 100
                                {
                                    texFile = charSectionsStore.FirstOrDefault(x => x.RaceID == displayinfoextra.Race && x.SexId == displayinfoextra.Gender
                                    &&  x.ColorIndex == displayinfoextra.HairColor && x.VariationIndex == displayinfoextra.HairStyle && x.BaseSection == 3)?.TextureName1;
                                }

                            }
                        }

                        // TITI, set creature texture
                        else if (textureDefType == M2Texture.TextureType.TEX_COMPONENT_MONSTER_1) // creature skin1
                        {
                            if (creatureDisplayInfoStore.Contains(displayid))
                            {
                                texFile = m2FilePath.Replace(m2FilePath.Split('\\').Last(), creatureDisplayInfoStore[displayid].TextureVariation1
                                    + ".blp", StringComparison.InvariantCultureIgnoreCase); // replace m2 name by tetxure name
                            }
                        }

                        else if (textureDefType == M2Texture.TextureType.TEX_COMPONENT_MONSTER_2) // creature skin2
                        {
                            if (creatureDisplayInfoStore.Contains(displayid))
                            {
                                texFile = m2FilePath.Replace(m2FilePath.Split('\\').Last(), creatureDisplayInfoStore[displayid].TextureVariation2
                                    + ".blp", StringComparison.InvariantCultureIgnoreCase); // replace m2 name by tetxure name
                            }
                        }

                        else if (textureDefType == M2Texture.TextureType.TEX_COMPONENT_MONSTER_3) // creature skin3
                        {
                            if (creatureDisplayInfoStore.Contains(displayid))
                            {
                                texFile = m2FilePath.Replace(m2FilePath.Split('\\').Last(), creatureDisplayInfoStore[displayid].TextureVariation3
                                    + ".blp", StringComparison.InvariantCultureIgnoreCase); // replace m2 name by tetxure name
                            }
                        }

                        else
                        {
                            // Console.WriteLine("Wasn't able to set texture for display id " + displayid);
                            // Console.WriteLine("texture type : " + textureDefType + " not implemented yet");
                        }

                        // System.Diagnostics.Debug.WriteLine($"M2 texture path :  {texFile}");
                        // var texFile = textureDef.filename.AsString();
                        var tcs = new TaskCompletionSource<TextureHandle>();
                        if (string.IsNullOrWhiteSpace(texFile))
                        {
                            Console.WriteLine("texture path is empty for display id : " + displayid + " dispextra : " + creatureDisplayInfoStore[displayid].ExtendedDisplayInfoID);
                            Console.WriteLine("texture type : " + textureDefType);
                            Console.WriteLine("skin section id : " + sectionSkinSectionId);
                        }
                        yield return textureManager.GetTexture(texFile ?? "", tcs);
                        var resTex = tcs.Task.Result;
                        if (th.HasValue)
                            th2 = resTex;
                        else
                            th = resTex;
                    }
                }

                var material = CreateMaterial(m2, in batch, th, th2);

                materials[j - 1] = material;
            }

            if (j == 0)
            {
                Console.WriteLine("Model " + m2FilePath + " has 0 materials");
                creaturemeshes[displayid] = null;
                completion.SetResult(null);
                creatureMeshesCurrentlyLoaded.Remove(displayid);
                result.SetResult(null);
                yield break;
            }

            mesh.RebuildIndices();

            var mdx = new MdxInstance
            {
                mesh = mesh,
                materials = materials.AsSpan(0, j).ToArray(),
                model = m2,
                attachments = attachments,
                scale = creatureDisplayInfo.CreatureModelScale
            };
            creaturemeshes.Add(displayid, mdx); // titi test
            completion.SetResult(mdx);
            creatureMeshesCurrentlyLoaded.Remove(displayid);
            result.SetResult(mdx);    
        }

        private Material CreateMaterial(M2 m2, in M2Batch batch, TextureHandle? textureHandle1, TextureHandle? textureHandle2)
        {
            ref readonly var materialDef = ref m2.materials[batch.materialIndex];
            var material = materialManager.CreateMaterial("data/m2.json");

            material.SetBuffer("boneMatrices", identityBonesBuffer);
            material.SetTexture("texture1", textureHandle1 ?? textureManager.EmptyTexture);
            material.SetTexture("texture2", textureHandle2 ?? textureManager.EmptyTexture);

            var trans = 1.0f;
            if (batch.colorIndex != -1 && m2.colors.Length < batch.colorIndex)
            {
                if (m2.colors[batch.colorIndex].alpha.values.Length == 0 ||
                    m2.colors[batch.colorIndex].alpha.values[0].Length == 0)
                    trans = 1;
                else
                    trans = m2.colors[batch.colorIndex].alpha.values[0][0].Value;
            }

            if (batch.textureTransparencyLookupId != -1 && m2.textureWeights.Length < batch.textureTransparencyLookupId)
            {
                if (m2.textureWeights[batch.textureTransparencyLookupId].weight.values.Length > 0 &&
                    m2.textureWeights[batch.textureTransparencyLookupId].weight.values[0].Length > 0)
                    trans *= m2.textureWeights[batch.textureTransparencyLookupId].weight.values[0][0].Value;
            }

            Vector4 mesh_color = new Vector4(1.0f, 1.0f, 1.0f, trans);

            material.SetUniform("mesh_color", mesh_color);
            var shaderId = ResolveShaderID1(batch.shaderId, m2, in batch, (m2.global_flags & M2Flags.FLAG_USE_TEXTURE_COMBINER_COMBOS) != 0, (int)materialDef.blending_mode);
            var shaders = ConvertShaderIDs(m2, in batch, shaderId);
            material.SetUniformInt("pixel_shader", (int)shaders.Item2);
            //Console.WriteLine(path + " INDEX: " + j + " Pixel shader: " + M2GetPixelShaderID(batch.textureCount, batch.shader_id) + " tex count: " + batch.textureCount + " shader id: " + batch.shader_id + " blend: " + materialDef.blending_mode + " priority " + batch.priorityPlane + " start ");

            material.SetUniform("highlight", 0);
            material.SetUniform("notSupported", 0);
            if (materialDef.blending_mode == M2Blend.M2BlendOpaque)
            {
                material.BlendingEnabled = false;
                material.SetUniform("alphaTest", -1);
            }
            else if (materialDef.blending_mode == M2Blend.M2BlendAlphaKey)
            {
                material.BlendingEnabled = false;
                //material.SourceBlending = Blending.One;
                //material.DestinationBlending = Blending.Zero;
                material.SetUniform("alphaTest", 224.0f / 255.0f);
            }
            else if (materialDef.blending_mode == M2Blend.M2BlendAlpha)
            {
                material.BlendingEnabled = true;
                material.SourceBlending = Blending.SrcAlpha;
                material.DestinationBlending = Blending.OneMinusSrcAlpha;
                material.SetUniform("alphaTest", 1.0f / 255.0f);
            }
            else if (materialDef.blending_mode == M2Blend.M2BlendNoAlphaAdd)
            {
                material.BlendingEnabled = true;
                material.SourceBlending = Blending.One;
                material.DestinationBlending = Blending.One;
                material.SetUniform("alphaTest", 1.0f / 255.0f);
            }
            else if (materialDef.blending_mode == M2Blend.M2BlendAdd)
            {
                material.BlendingEnabled = true;
                material.SourceBlending = Blending.SrcAlpha;
                material.DestinationBlending = Blending.One;
                material.SetUniform("alphaTest", 1.0f / 255.0f);
            }
            else if (materialDef.blending_mode == M2Blend.M2BlendMod)
            {
                material.BlendingEnabled = true;
                material.SourceBlending = Blending.DstColor;
                material.DestinationBlending = Blending.Zero;
                material.SetUniform("alphaTest", 1.0f / 255.0f);
            }
            else if (materialDef.blending_mode == M2Blend.M2BlendMod2X)
            {
                material.BlendingEnabled = true;
                material.SourceBlending = Blending.DstColor;
                material.DestinationBlending = Blending.SrcColor;
                material.SetUniform("alphaTest", 1.0f / 255.0f);
            }
            else
                material.SetUniform("notSupported", 1);

            material.ZWrite = !material.BlendingEnabled;
            //material.DepthTesting = materialDef.flags.HasFlagFast(M2MaterialFlags.DepthTest); // produces wrong results :thonk:

            if (materialDef.flags.HasFlagFast(M2MaterialFlags.TwoSided))
                material.Culling = CullingMode.Off;
            material.SetUniformInt("unlit", materialDef.flags.HasFlagFast(M2MaterialFlags.Unlit) ? 1 : 0);
            return material;
        }

        public IEnumerator LoadItemMesh(uint displayid, bool right, uint race, uint gender, TaskCompletionSource<MdxInstance?> result)
        {
            ushort raceGenderKey = (ushort)(race << 1 | gender);
            if (itemMeshes.ContainsKey((displayid, right, raceGenderKey)))
            {
                result.SetResult(itemMeshes[(displayid, right, raceGenderKey)]);
                yield break;
            }

            if (itemMeshesCurrentlyLoaded.TryGetValue((displayid, right, raceGenderKey), out var loadInProgress))
            {
                yield return new WaitForTask(loadInProgress);
                result.SetResult(itemMeshes[(displayid, right, raceGenderKey)]);
                yield break;
            }

            var completion = new TaskCompletionSource<MdxInstance?>();
            itemMeshesCurrentlyLoaded[(displayid, right, raceGenderKey)] = completion.Task;

            if (!itemDisplayInfoStore.TryGetValue(displayid, out var displayInfo))
            {
                Console.WriteLine("Cannot find item display id " + displayid);
                itemMeshes[(displayid, right, raceGenderKey)] = null;
                completion.SetResult(null);
                itemMeshesCurrentlyLoaded.Remove((displayid, right, raceGenderKey));
                result.SetResult(null);
                yield break;
            }

            if ((string.IsNullOrEmpty(displayInfo.LeftModel) && !right) ||
                (string.IsNullOrEmpty(displayInfo.RightModel) && right))
            {
                itemMeshes[(displayid, right, raceGenderKey)] = null;
                completion.SetResult(null);
                itemMeshesCurrentlyLoaded.Remove((displayid, right, raceGenderKey));
                result.SetResult(null);
                yield break;
            }

            // don't know what is the right way to determine the folder name
            // that works for 3.3.5 tho
            var folderPath = "ITEM\\OBJECTCOMPONENTS\\";
            var model = (right ? displayInfo.RightModel : displayInfo.LeftModel).ToLower();
            var texture = right ? displayInfo.RightModelTexture : displayInfo.LeftModelTexture;

            if (model.StartsWith("arrow") || model.StartsWith("bullet"))
                folderPath += "AMMO\\";
            else if (model.StartsWith("helm"))
            {
                folderPath += "HEAD\\";
                if (racesStore.TryGetValue(race, out var raceInfo))
                {
                    model = Path.ChangeExtension(model, null) + "_" + raceInfo.ClientPrefix + (gender == 0 ? "M" : "F") + ".M2";
                }
                else
                {
                    model = Path.ChangeExtension(model, null) + "_m.M2";
                    Console.WriteLine("Trying to load a helm, without race!");
                }
            }
            else if (model.StartsWith("pouch"))
                folderPath += "Pouch\\";
            else if (model.StartsWith("shield") || model.StartsWith("buckler"))
                folderPath += "Shield\\";
            else if (model.StartsWith("lshoulder") || model.StartsWith("rshoulder"))
                folderPath += "Shoulder\\";
            else
                folderPath += "WEAPON\\";
            
            var m2FilePath = folderPath + model;
            var texturePath = folderPath + texture + ".blp";
            
            m2FilePath = m2FilePath.Replace("mdx", "M2", StringComparison.InvariantCultureIgnoreCase);
            m2FilePath = m2FilePath.Replace("mdl", "M2", StringComparison.InvariantCultureIgnoreCase); // apaprently there are still some MDL models
            
            var skinFilePath = m2FilePath.Replace(".m2", "00.skin", StringComparison.InvariantCultureIgnoreCase);
            var file = gameFiles.ReadFile(m2FilePath);

            yield return new WaitForTask(file);

            var skinFile = gameFiles.ReadFile(skinFilePath);
            
            yield return new WaitForTask(skinFile);
            
            if (file.Result == null || skinFile.Result == null)
            {
                Console.WriteLine("Cannot find model " + m2FilePath);
                itemMeshes[(displayid, right, raceGenderKey)] = null;
                completion.SetResult(null);
                itemMeshesCurrentlyLoaded.Remove((displayid, right, raceGenderKey));
                result.SetResult(null);
                yield break;
            }

            M2 m2 = null!;
            M2Skin skin = new();
            Vector3[] vertices = null!;
            Vector3[] normals = null!;
            Vector2[] uv1 = null!;
            Vector2[] uv2 = null!;
            Color[] boneWeights = null!;
            Color[] boneIndices = null!;

            yield return new WaitForTask(Task.Run(() =>
            {
                m2 = M2.Read(new MemoryBinaryReader(file.Result), m2FilePath, p =>
                {
                    // TODO: can I use ReadFileSync? Can be problematic...
                    var bytes = gameFiles.ReadFileSyncLocked(p, true);
                    if (bytes == null)
                        return null;
                    return new MemoryBinaryReader(bytes);
                });
                file.Result.Dispose();
                skin = new M2Skin(new MemoryBinaryReader(skinFile.Result));
                skinFile.Result.Dispose();
                var count = m2.vertices.Length;
                vertices = new Vector3[count];
                normals = new Vector3[count];
                uv1 = new Vector2[count];
                uv2 = new Vector2[count];
                boneWeights = new Color[count];
                boneIndices = new Color[count];
                
                for (int i = 0; i < count; ++i)
                {
                    ref readonly var vert = ref m2.vertices[skin.Vertices[i]];
                    vertices[i] = vert.pos;
                    normals[i] = vert.normal;
                    uv1[i] = vert.tex_coord1;
                    uv2[i] = vert.tex_coord2;
                    boneWeights[i] = vert.bone_weights;
                    boneIndices[i] = vert.bone_indices;
                }
            }));
            
            var md = new MeshData(vertices, normals, uv1, new ushort[] { }, null, null, uv2, boneWeights, boneIndices);
            
            var mesh = meshManager.CreateMesh(md);
            mesh.SetSubmeshCount(skin.Batches.Length);

            Material[] materials = new Material[skin.Batches.Length];
            int j = 0;
            foreach (var batch in skin.Batches)
            {
                if (batch.skinSectionIndex == ushort.MaxValue ||
                    batch.materialIndex >= m2.materials.Length ||
                    batch.skinSectionIndex >= skin.SubMeshes.Length)
                {
                    Console.WriteLine("Sth wrong with batch " + j + " in model " + m2FilePath);
                    continue;
                }

                var sectionIndexCount = skin.SubMeshes[batch.skinSectionIndex].indexCount;
                var sectionIndexStart = skin.SubMeshes[batch.skinSectionIndex].indexStart;

                mesh.SetIndices(skin.Indices.AsSpan(sectionIndexStart, Math.Min(sectionIndexCount, skin.Indices.Length - sectionIndexStart)), j++);
                
                TextureHandle? th = null;
                TextureHandle? th2 = null;
                for (int i = 0; i < (batch.textureCount >= 5 ? 1 : batch.textureCount); ++i)
                {
                    if (batch.textureLookupId + i >= m2.textureLookupTable.Length)
                    {
                        if (th.HasValue)
                            th2 = textureManager.EmptyTexture;
                        else
                            th = textureManager.EmptyTexture;
                        Console.WriteLine("File " + m2FilePath + " batch " + j + " tex " + i + " out of range");
                        continue;
                    }
                    var texId = m2.textureLookupTable[batch.textureLookupId + i];
                    if (texId == -1)
                    {
                        if (th.HasValue)
                            th2 = textureManager.EmptyTexture;
                        else
                            th = textureManager.EmptyTexture;
                    }
                    else
                    {
                        var textureDefType = m2.textures[texId].type;
                        string texFile = "";
                        if (textureDefType == 0) // if tetx is hardcoded
                            texFile = m2.textures[texId].filename.AsString();
                        else
                        {
                            if (textureDefType != M2Texture.TextureType.TEX_COMPONENT_OBJECT_SKIN)
                                Console.WriteLine("okay, so there is model " + m2FilePath + " which has texture type: " + textureDefType + ". What is it?");
                            texFile = texturePath;
                        }
                        
                        var tcs = new TaskCompletionSource<TextureHandle>();
                        yield return textureManager.GetTexture(texFile, tcs);
                        var resTex = tcs.Task.Result;
                        if (th.HasValue)
                            th2 = resTex;
                        else
                            th = resTex;
                    }
                }

                materials[j - 1] = CreateMaterial(m2, in batch, th, th2);
            }
            
            if (j == 0)
            {
                Console.WriteLine("Model " + m2FilePath + " has 0 materials");
                itemMeshes[(displayid, right, raceGenderKey)] = null;
                completion.SetResult(null);
                itemMeshesCurrentlyLoaded.Remove((displayid, right, raceGenderKey));
                result.SetResult(null);
                yield break;
            }

            mesh.RebuildIndices();

            var mdx = new MdxInstance
            {
                mesh = mesh,
                materials = materials.AsSpan(0, j).ToArray(),
                model = m2
            };
            itemMeshes.Add((displayid, right, raceGenderKey), mdx);
            completion.SetResult(null);
            itemMeshesCurrentlyLoaded.Remove((displayid, right, raceGenderKey));
            result.SetResult(mdx);
        }
        
        public IEnumerator LoadGameObjectModel(uint gameObjectDisplayId, TaskCompletionSource<MdxInstance?> result)
        {
            if (gameObjectmeshes.ContainsKey(gameObjectDisplayId))
            {
                result.SetResult(gameObjectmeshes[gameObjectDisplayId]);
                yield break;
            }

            if (gameObjectMeshesCurrentlyLoaded.TryGetValue(gameObjectDisplayId, out var loadInProgress))
            {
                yield return new WaitForTask(loadInProgress);
                result.SetResult(gameObjectmeshes[gameObjectDisplayId]);
                yield break;
            }

            var completion = new TaskCompletionSource<MdxInstance?>();
            gameObjectMeshesCurrentlyLoaded[gameObjectDisplayId] = completion.Task;

            if (!gameObjectDisplayInfoStore.TryGetValue(gameObjectDisplayId, out var displayInfo))
            {
                Console.WriteLine("Cannot find model " + gameObjectDisplayId);
                gameObjectmeshes[gameObjectDisplayId] = null;
                completion.SetResult(null);
                gameObjectMeshesCurrentlyLoaded.Remove(gameObjectDisplayId);
                result.SetResult(null);
                yield break;
            }

            if (displayInfo.ModelName.EndsWith("wmo", StringComparison.InvariantCultureIgnoreCase))
            {
                Console.WriteLine("Gameobject display id " + gameObjectDisplayId + " is WMO, not M2. this is NOT YET supported!! path: " + displayInfo.ModelName);
                gameObjectmeshes[gameObjectDisplayId] = null;
                completion.SetResult(null);
                gameObjectMeshesCurrentlyLoaded.Remove(gameObjectDisplayId);
                result.SetResult(null);
                yield break;
            }
            
            var m2FilePath = displayInfo.ModelName.Replace("mdx", "M2", StringComparison.InvariantCultureIgnoreCase);
            m2FilePath = m2FilePath.Replace("mdl", "M2", StringComparison.InvariantCultureIgnoreCase); // apparently there are still some MDL models	
            var skinFilePath = m2FilePath.Replace(".m2", "00.skin", StringComparison.InvariantCultureIgnoreCase);
            var file = gameFiles.ReadFile(m2FilePath);

            yield return new WaitForTask(file);

            var skinFile = gameFiles.ReadFile(skinFilePath);
            
            yield return new WaitForTask(skinFile);
            
            if (file.Result == null || skinFile.Result == null)
            {
                Console.WriteLine("Cannot find path " + displayInfo.ModelName);
                gameObjectmeshes[gameObjectDisplayId] = null;
                completion.SetResult(null);
                gameObjectMeshesCurrentlyLoaded.Remove(gameObjectDisplayId);
                result.SetResult(null);
                yield break;
            }

            M2 m2 = null!;
            M2Skin skin = new();
            Vector3[] vertices = null!;
            Vector3[] normals = null!;
            Vector2[] uv1 = null!;
            Vector2[] uv2 = null!;
            Color[] boneWeights = null!;
            Color[] boneIndices = null!;

            yield return new WaitForTask(Task.Run(() =>
            {
                m2 = M2.Read(new MemoryBinaryReader(file.Result), m2FilePath, p =>
                {
                    // TODO: can I use ReadFileSync? Can be problematic...
                    var bytes = gameFiles.ReadFileSyncLocked(p, true);
                    if (bytes == null)
                        return null;
                    return new MemoryBinaryReader(bytes);
                });
                file.Result.Dispose();
                skin = new M2Skin(new MemoryBinaryReader(skinFile.Result));
                skinFile.Result.Dispose();
                var count = m2.vertices.Length;
                vertices = new Vector3[count];
                normals = new Vector3[count];
                uv1 = new Vector2[count];
                uv2 = new Vector2[count];
                boneWeights = new Color[count];
                boneIndices = new Color[count];
                
                for (int i = 0; i < count; ++i)
                {
                    ref readonly var vert = ref m2.vertices[skin.Vertices[i]];
                    vertices[i] = vert.pos;
                    normals[i] = vert.normal;
                    uv1[i] = vert.tex_coord1;
                    uv2[i] = vert.tex_coord2;
                    boneWeights[i] = vert.bone_weights;
                    boneIndices[i] = vert.bone_indices;
                }
            }));
            
            var md = new MeshData(vertices, normals, uv1, new ushort[] { }, null, null, uv2, boneWeights, boneIndices);
            
            var mesh = meshManager.CreateMesh(md);
            mesh.SetSubmeshCount(skin.Batches.Length);

            Material[] materials = new Material[skin.Batches.Length];
            int j = 0;
            foreach (var batch in skin.Batches)
            {
                if (batch.skinSectionIndex == ushort.MaxValue ||
                    batch.materialIndex >= m2.materials.Length ||
                    batch.skinSectionIndex >= skin.SubMeshes.Length)
                {
                    Console.WriteLine("Sth wrong with batch " + j + " in model " + gameObjectDisplayId);
                    continue;
                }

                var sectionIndexCount = skin.SubMeshes[batch.skinSectionIndex].indexCount;
                var sectionIndexStart = skin.SubMeshes[batch.skinSectionIndex].indexStart;
                
                mesh.SetIndices(skin.Indices.AsSpan(sectionIndexStart, Math.Min(sectionIndexCount, skin.Indices.Length - sectionIndexStart)), j++);
                
                TextureHandle? th = null;
                TextureHandle? th2 = null;
                for (int i = 0; i < (batch.textureCount >= 5 ? 1 : batch.textureCount); ++i)
                {
                    if (batch.textureLookupId + i >= m2.textureLookupTable.Length)
                    {
                        if (th.HasValue)
                            th2 = textureManager.EmptyTexture;
                        else
                            th = textureManager.EmptyTexture;
                        Console.WriteLine("File " + gameObjectDisplayId + " batch " + j + " tex " + i + " out of range");
                        continue;
                    }
                    var texId = m2.textureLookupTable[batch.textureLookupId + i];
                    if (texId == -1)
                    {
                        if (th.HasValue)
                            th2 = textureManager.EmptyTexture;
                        else
                            th = textureManager.EmptyTexture;
                    }
                    else
                    {
                        var texFile = m2.textures[texId].filename.AsString();
                        var tcs = new TaskCompletionSource<TextureHandle>();
                        yield return textureManager.GetTexture(texFile, tcs);
                        var resTex = tcs.Task.Result;
                        if (th.HasValue)
                            th2 = resTex;
                        else
                            th = resTex;
                    }
                }

                materials[j - 1] = CreateMaterial(m2, in batch, th, th2);
            }

            if (j == 0)
            {
                Console.WriteLine("Model " + m2FilePath + " has 0 materials");
                gameObjectmeshes[gameObjectDisplayId] = null;
                completion.SetResult(null);
                gameObjectMeshesCurrentlyLoaded.Remove(gameObjectDisplayId);
                result.SetResult(null);
                yield break;
            }
            
            mesh.RebuildIndices();

            var mdx = new MdxInstance
            {
                mesh = mesh,
                materials = materials.AsSpan(0, j).ToArray(),
                model = m2
            };
            gameObjectmeshes.Add(gameObjectDisplayId, mdx);
            completion.SetResult(null);
            gameObjectMeshesCurrentlyLoaded.Remove(gameObjectDisplayId);
            result.SetResult(mdx);
        }
        
        public IEnumerator LoadM2Mesh(string path, TaskCompletionSource<MdxInstance?> result)
        {
            if (meshes.ContainsKey(path))
            {
                result.SetResult(meshes[path]);
                yield break;
            }

            if (meshesCurrentlyLoaded.TryGetValue(path, out var loadInProgress))
            {
                yield return new WaitForTask(loadInProgress);
                result.SetResult(meshes[path]);
                yield break;
            }

            var completion = new TaskCompletionSource<MdxInstance?>();
            meshesCurrentlyLoaded[path] = completion.Task;

            var m2FilePath = path.Replace("mdx", "M2", StringComparison.InvariantCultureIgnoreCase);
            m2FilePath = m2FilePath.Replace("mdl", "M2", StringComparison.InvariantCultureIgnoreCase); // apparently there are still some MDL models	
            var skinFilePath = m2FilePath.Replace(".m2", "00.skin", StringComparison.InvariantCultureIgnoreCase);
            var file = gameFiles.ReadFile(m2FilePath);

            yield return new WaitForTask(file);

            var skinFile = gameFiles.ReadFile(skinFilePath);
            
            yield return new WaitForTask(skinFile);
            
            if (file.Result == null || skinFile.Result == null)
            {
                Console.WriteLine("Cannot find model " + path);
                meshes[path] = null;
                completion.SetResult(null);
                meshesCurrentlyLoaded.Remove(path);
                result.SetResult(null);
                yield break;
            }

            M2 m2 = null!;
            M2Skin skin = new();
            Vector3[] vertices = null!;
            Vector3[] normals = null!;
            Vector2[] uv1 = null!;
            Vector2[] uv2 = null!;
            Color[] boneWeights = null!;
            Color[] boneIndices = null!;

            yield return new WaitForTask(Task.Run(() =>
            {
                m2 = M2.Read(new MemoryBinaryReader(file.Result), m2FilePath, p =>
                {
                    // TODO: can I use ReadFileSync? Can be problematic...
                    var bytes = gameFiles.ReadFileSyncLocked(p, true);
                    if (bytes == null)
                        return null;
                    return new MemoryBinaryReader(bytes);
                });
                file.Result.Dispose();
                skin = new M2Skin(new MemoryBinaryReader(skinFile.Result));
                skinFile.Result.Dispose();
                var count = m2.vertices.Length;
                vertices = new Vector3[count];
                normals = new Vector3[count];
                uv1 = new Vector2[count];
                uv2 = new Vector2[count];
                boneWeights = new Color[count];
                boneIndices = new Color[count];
                
                for (int i = 0; i < count; ++i)
                {
                    ref readonly var vert = ref m2.vertices[skin.Vertices[i]];
                    vertices[i] = vert.pos;
                    normals[i] = vert.normal;
                    uv1[i] = vert.tex_coord1;
                    uv2[i] = vert.tex_coord2;
                    boneWeights[i] = vert.bone_weights;
                    boneIndices[i] = vert.bone_indices;
                }
            }));
            
            var md = new MeshData(vertices, normals, uv1, new ushort[] { }, null, null, uv2, boneWeights, boneIndices);
            
            var mesh = meshManager.CreateMesh(md);
            mesh.SetSubmeshCount(skin.Batches.Length);

            Material[] materials = new Material[skin.Batches.Length];
            int j = 0;
            foreach (var batch in skin.Batches)
            {
                if (batch.skinSectionIndex == ushort.MaxValue ||
                    batch.materialIndex >= m2.materials.Length ||
                    batch.skinSectionIndex >= skin.SubMeshes.Length)
                {
                    Console.WriteLine("Sth wrong with batch " + j + " in model " + path);
                    continue;
                }

                var sectionIndexCount = skin.SubMeshes[batch.skinSectionIndex].indexCount;
                var sectionIndexStart = skin.SubMeshes[batch.skinSectionIndex].indexStart;
                
                mesh.SetIndices(skin.Indices.AsSpan(sectionIndexStart, Math.Min(sectionIndexCount, skin.Indices.Length - sectionIndexStart)), j++);

                TextureHandle? th = null;
                TextureHandle? th2 = null;
                for (int i = 0; i < (batch.textureCount >= 5 ? 1 : batch.textureCount); ++i)
                {
                    if (batch.textureLookupId + i >= m2.textureLookupTable.Length)
                    {
                        if (th.HasValue)
                            th2 = textureManager.EmptyTexture;
                        else
                            th = textureManager.EmptyTexture;
                        Console.WriteLine("File " + path + " batch " + j + " tex " + i + " out of range");
                        continue;
                    }
                    var texId = m2.textureLookupTable[batch.textureLookupId + i];
                    if (texId == -1)
                    {
                        if (th.HasValue)
                            th2 = textureManager.EmptyTexture;
                        else
                            th = textureManager.EmptyTexture;
                    }
                    else
                    {
                        var texFile = m2.textures[texId].filename.AsString();
                        var tcs = new TaskCompletionSource<TextureHandle>();
                        yield return textureManager.GetTexture(texFile, tcs);
                        var resTex = tcs.Task.Result;
                        if (th.HasValue)
                            th2 = resTex;
                        else
                            th = resTex;
                    }
                }

                materials[j - 1] = CreateMaterial(m2, in batch, th, th2);
            }
            
            if (j == 0)
            {
                Console.WriteLine("Model " + m2FilePath + " has 0 materials");
                meshes[path] = null;
                completion.SetResult(null);
                meshesCurrentlyLoaded.Remove(path);
                result.SetResult(null);
                yield break;
            }

            mesh.RebuildIndices();

            var mdx = new MdxInstance
            {
                mesh = mesh,
                materials = materials.AsSpan(0, j).ToArray(),
                model = m2
            };
            meshes.Add(path, mdx);
            completion.SetResult(null);
            meshesCurrentlyLoaded.Remove(path);
            result.SetResult(mdx);
        }
        
        public void RenderGUI()
        {
        }
        
        ushort ResolveShaderID1(ushort shaderId, M2 m2, in M2Batch textureUnit, bool Use_Texture_Combiner_Combos, int blendingMode)
        {
            // According to Wowdev.wiki textureUnits with shaderID 0x8000 should not be rendered
            if ((shaderId & 0x8000) != 0)
                return shaderId;

            ushort shaderID = shaderId;

            if (!Use_Texture_Combiner_Combos)
            {
                ushort textureUnitValue = m2.textureUnitLookupTable[textureUnit.textureUnitLookupId];

                bool envMapped = textureUnitValue == short.MaxValue;
                bool isTransparent = blendingMode != 0;

                if (isTransparent)
                {
                    shaderID = 0x01;

                    if (envMapped)
                        shaderID |= 0x08;

                    shaderID *= 0x10;
                }

                if (textureUnitValue == 1)
                {
                    shaderID |= 0x4000;
                }
            }
            else
            {
                // Name is guessed based on usage below
                Span<int> blendOverrideModifier = stackalloc int[2];

                for (int i = 0; i < textureUnit.textureCount; i++)
                {
                    int blendOverride = m2.textureCombinerCombos!.Value[i + shaderId];

                    if (i == 0 && blendingMode == 0)
                        blendOverride = 0;

                    ushort textureUnitValue = m2.textureUnitLookupTable[i + textureUnit.textureUnitLookupId];
                    bool isEnvMapped = textureUnitValue == short.MaxValue;
                    bool isTransparent = textureUnitValue == 1;

                    blendOverrideModifier[i] = blendOverride | ((isEnvMapped ? 1 : 0) * 8);

                    if (isTransparent && i + 1 == textureUnit.textureCount)
                        shaderID |= 0x4000;

                }

                shaderID |= (ushort)((blendOverrideModifier[0] * 16) | blendOverrideModifier[1]);
            }

            return shaderID;
        }
        
        (ModelVertexShader, ModelPixelShader) ConvertShaderIDs(M2 m2, in M2Batch batch, ushort batchShaderId)
        {
            // If the shaderId is 0x8000 we don't need to map anything
            if (batchShaderId == 0x8000)
                return (ModelVertexShader.Diffuse_T1, ModelPixelShader.Opaque);

            var shaderId = (ushort)(batchShaderId & 0x7FFF);
            ushort textureCount = batch.textureCount;

            ModelPixelShader ps = ModelPixelShader.Mod;
            ModelVertexShader vs = ModelVertexShader.Diffuse_Env;
            
            if ((batchShaderId & 0x8000) != 0)
            {
                if (shaderId == 1)
                {
                    vs = ModelVertexShader.Diffuse_T1_Env;
                    ps = ModelPixelShader.Opaque_Mod2xNA_Alpha;
                }
                else if (shaderId == 2)
                {
                    vs = ModelVertexShader.Diffuse_T1_Env;
                    ps = ModelPixelShader.Opaque_AddAlpha;
                }
                else if (shaderId == 3)
                {
                    vs = ModelVertexShader.Diffuse_T1_Env;
                    ps = ModelPixelShader.Opaque_AddAlpha_Alpha;
                }
                else
                {
                    Console.WriteLine("Wrong shader id..?");
                }
            }
            else
            {
                if (textureCount == 0)
                {
                    Console.WriteLine("Fatal: batch has 0 textures");
                }

                ushort t1PixelMode = (ushort)((shaderId >> 4) & 0x7);
                bool t1EnvMapped = ((shaderId >> 4) & 0x8) != 0;
                ushort textureUnitValue = m2.textureUnitLookupTable[batch.textureUnitLookupId];

                if (textureCount == 1)
                {
                    // Resolve Vertex Shader Id
                    {
                        if (t1PixelMode != 0)
                        {
                            vs = ModelVertexShader.Diffuse_Env;
                        }
                        else if (textureUnitValue == 0)
                        {
                            vs = ModelVertexShader.Diffuse_T1;
                        }
                        else
                        {
                            vs = ModelVertexShader.Diffuse_T2;
                        }
                    }

                    // Resolve Pixel Shader Id
                    {
                        if (t1PixelMode == 0)
                        {
                            ps = ModelPixelShader.Opaque;
                        }
                        /*else if (t1PixelMode == 1)
                        {
                            textureUnit.pixelShaderId = PixelShaderID::Mod;
                        }*/
                        else if (t1PixelMode == 2)
                        {
                            ps = ModelPixelShader.Decal;
                        }
                        else if (t1PixelMode == 3)
                        {
                            ps = ModelPixelShader.Add;
                        }
                        else if (t1PixelMode == 4)
                        {
                            ps = ModelPixelShader.Mod2x;
                        }
                        else if (t1PixelMode == 5)
                        {
                            ps = ModelPixelShader.Fade;
                        }
                        else
                        {
                            ps = ModelPixelShader.Mod;
                        }
                    }
                }
                else
                {
                    ushort t2PixelMode = (ushort)(shaderId & 0x7);
                    bool t2EnvMapped = (shaderId & 0x8) != 0;

                    // Resolve Vertex Shader Id
                    {
                        if (t1EnvMapped && !t2EnvMapped)
                        {
                            vs = ModelVertexShader.Diffuse_Env_T2;
                        }
                        else if (!t1EnvMapped && t2EnvMapped)
                        {
                            vs = ModelVertexShader.Diffuse_T1_Env;
                        }
                        else if (t1EnvMapped && t2EnvMapped)
                        {
                            vs = ModelVertexShader.Diffuse_Env_Env;
                        }
                        else
                        {
                            vs = ModelVertexShader.Diffuse_T1_T2;
                        }
                    }

                    // Resolve Pixel Shader Id
                    {
                        if (t1PixelMode == 0)
                        {
                            if (t2PixelMode == 0)
                            {
                                ps = ModelPixelShader.Opaque_Opaque;
                            }
                            /*else if (t2PixelMode == 1)
                            {
                                textureUnit.pixelShaderId = PixelShaderID::Opaque_Mod;
                            }*/
                            else if (t2PixelMode == 3)
                            {
                                ps = ModelPixelShader.Opaque_Add;
                            }
                            else if (t2PixelMode == 4)
                            {
                                ps = ModelPixelShader.Mod2x;
                            }
                            else if (t2PixelMode == 6)
                            {
                                ps = ModelPixelShader.Opaque_Mod2xNA;
                            }
                            else if (t2PixelMode == 7)
                            {
                                ps = ModelPixelShader.Opaque_AddNA;
                            }
                            else
                            {
                                ps = ModelPixelShader.Opaque_Mod;
                            }
                        }
                        else if (t1PixelMode == 1)
                        {
                            if (t2PixelMode == 0)
                            {
                                ps = ModelPixelShader.Mod_Opaque;
                            }
                            else if (t2PixelMode == 3)
                            {
                                ps = ModelPixelShader.Mod_Add;
                            }
                            else if (t2PixelMode == 4)
                            {
                                ps = ModelPixelShader.Mod_Mod2x;
                            }
                            else if (t2PixelMode == 6)
                            {
                                ps = ModelPixelShader.Mod_Mod2xNA;
                            }
                            else if (t2PixelMode == 7)
                            {
                                ps = ModelPixelShader.Mod_AddNA;
                            }
                            else
                            {
                                ps = ModelPixelShader.Mod_Mod;
                            }
                        }
                        else if (t1PixelMode == 3 && t2PixelMode == 1)
                        {
                            ps = ModelPixelShader.Add_Mod;
                        }
                        else if (t1PixelMode == 4 && t2PixelMode == 1)
                        {
                            ps = ModelPixelShader.Mod2x_Mod;
                        }
                        else if (t1PixelMode == 4 && t2PixelMode == 4)
                        {
                            ps = ModelPixelShader.Mod2x_Mod2x;
                        }
                        else
                        {
                            Console.WriteLine("Unknown pixel shader combination: {0}, {1} shader: {2}", t1PixelMode, t2PixelMode, shaderId);
                        }
                    }
                }
            }

            return (vs, ps);
        }
        
        public void Dispose()
        {
            identityBonesBuffer.Dispose();
            
            foreach (var mesh in meshes.Values)
                mesh?.Dispose(meshManager);

            foreach (var mesh in itemMeshes.Values)
                mesh?.Dispose(meshManager);

            foreach (var mesh in gameObjectmeshes.Values)
                mesh?.Dispose(meshManager);

            // titi test
            foreach (var creaturemesh in creaturemeshes.Values)
                creaturemesh?.Dispose(meshManager);
        }
    }
}
