﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Harmony;
using RimWorld;
using UnityEngine;
using Verse;

namespace CombatExtended.Harmony
{
    [HarmonyPatch(typeof(PawnRenderer), "RenderPawnInternal",
        typeof(Vector3), typeof(float), typeof(bool), typeof(Rot4), typeof(Rot4), typeof(RotDrawMode), typeof(bool), typeof(bool))]
    internal static class Harmony_PawnRenderer_RenderPawnInternal
    {
        private enum WriteState
        {
            None,
            WriteHead,
            WriteShell,
            WritePostShell
        }

        // Sync these with vanilla PawnRenderer constants
        private const float YOffsetBehind = 0.00390625f;
        private const float YOffsetHead = 0.02734375f;
        private const float YOffsetOnHead = 0.03125f;
        private const float YOffsetPostHead = 0.03515625f;
        private const float YOffsetIntervalClothes = 0.00390625f;

        private static void DrawHeadApparel(PawnRenderer renderer, Mesh mesh, Vector3 rootLoc, Vector3 headLoc, Vector3 headOffset, Rot4 bodyFacing, Quaternion quaternion, bool portrait, ref bool hideHair)
        {
            var apparelGraphics = renderer.graphics.apparelGraphics;
            var headwearGraphics = apparelGraphics.Where(a => a.sourceApparel.def.apparel.LastLayer.GetModExtension<ApparelLayerExtension>()?.IsHeadwear ?? false).ToArray();
            if (!headwearGraphics.Any())
                return;

            var interval = YOffsetIntervalClothes / headwearGraphics.Length;
            var headwearPos = headLoc;

            foreach (var apparelRecord in headwearGraphics)
            {
                if (!apparelRecord.sourceApparel.def.apparel.hatRenderedFrontOfFace)
                {
                    headwearPos.y += interval;
                    hideHair = true;
                    var apparelMat = apparelRecord.graphic.MatAt(bodyFacing);
                    apparelMat = renderer.graphics.flasher.GetDamagedMat(apparelMat);
                    GenDraw.DrawMeshNowOrLater(mesh, headwearPos, quaternion, apparelMat, portrait);
                }
                else
                {
                    var maskMat = apparelRecord.graphic.MatAt(bodyFacing);
                    maskMat = renderer.graphics.flasher.GetDamagedMat(maskMat);
                    var maskLoc = rootLoc + headOffset;
                    maskLoc.y += !(bodyFacing == Rot4.North) ? YOffsetPostHead : YOffsetBehind;
                    GenDraw.DrawMeshNowOrLater(mesh, maskLoc, quaternion, maskMat, portrait);
                }
            }
        }

        private static float GetPostShellOffset(PawnRenderer renderer)
        {
            var apparelGraphics = renderer.graphics.apparelGraphics.Where(a => a.sourceApparel.def.apparel.LastLayer.drawOrder >= ApparelLayerDefOf.Shell.drawOrder).ToList();
            return apparelGraphics.Any() ? YOffsetIntervalClothes / apparelGraphics.Count : 0;
        }

        private static bool IsPreShellLayer(ApparelLayerDef layer)
        {
            return layer.drawOrder < ApparelLayerDefOf.Shell.drawOrder
                   || (layer.GetModExtension<ApparelLayerExtension>()?.IsHeadwear ?? false);
        }

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var state = WriteState.None;
            foreach (var code in instructions)
            {
                if (state == WriteState.WriteHead)
                {
                    if (code.opcode == OpCodes.Ldloc_S && ((LocalBuilder)code.operand).LocalIndex == 13)
                    {
                        state = WriteState.None;

                        // Insert new calls for head renderer
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Ldloc_S, 14);
                        yield return new CodeInstruction(OpCodes.Ldarg_1);
                        yield return new CodeInstruction(OpCodes.Ldloc_S, 12);
                        yield return new CodeInstruction(OpCodes.Ldloc_S, 9);
                        yield return new CodeInstruction(OpCodes.Ldarg, 4);
                        yield return new CodeInstruction(OpCodes.Ldloc_0);
                        yield return new CodeInstruction(OpCodes.Ldarg, 7);
                        yield return new CodeInstruction(OpCodes.Ldloca_S, 13);
                        yield return new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(Harmony_PawnRenderer_RenderPawnInternal), nameof(DrawHeadApparel)));

                        yield return code;
                    }

                    continue;
                }

                if (state == WriteState.WriteShell)
                {
                    state = WriteState.WritePostShell;

                    // Write new calls for post shell rendering
                    code.opcode = OpCodes.Brtrue;
                }

                if (state == WriteState.WritePostShell && code.opcode == OpCodes.Call && code.operand == AccessTools.Method(typeof(GenDraw), nameof(GenDraw.DrawMeshNowOrLater)))
                {
                    state = WriteState.None;

                    yield return new CodeInstruction(OpCodes.Ldloca_S, 7);
                    yield return new CodeInstruction(OpCodes.Dup);
                    yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Vector3), nameof(Vector3.y)));
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(Harmony_PawnRenderer_RenderPawnInternal), nameof(GetPostShellOffset)));
                    yield return new CodeInstruction(OpCodes.Add);
                    yield return new CodeInstruction(OpCodes.Stfld, AccessTools.Field(typeof(Vector3), nameof(Vector3.y)));
                }

                if (code.opcode == OpCodes.Stloc_S && ((LocalBuilder)code.operand).LocalIndex == 14)
                {
                    state = WriteState.WriteHead;
                }
                else if (code.opcode == OpCodes.Ldsfld && code.operand == AccessTools.Field(typeof(ApparelLayerDefOf), nameof(ApparelLayerDefOf.Shell)))
                {
                    state = WriteState.WriteShell;
                    code.opcode = OpCodes.Callvirt;
                    code.operand = AccessTools.Method(typeof(Harmony_PawnRenderer_RenderPawnInternal), nameof(IsPreShellLayer));
                }

                yield return code;
            }
        }
    }

    [HarmonyPatch(typeof(PawnRenderer), "DrawEquipmentAiming")]
    internal static class Harmony_PawnRenderer_DrawEquipmentAiming
    {
        private static void DrawMeshModified(Mesh mesh, Vector3 position, Quaternion rotation, Material mat, int layer, Thing eq, float aimAngle)
        {
            var drawData = eq.def.GetModExtension<GunDrawExtension>() ?? new GunDrawExtension();
            var scale = new Vector3(drawData.DrawSize.x, 1, drawData.DrawSize.y);
            var posVec = new Vector3(drawData.DrawOffset.x, 0, drawData.DrawOffset.y);
            if (aimAngle > 200 && aimAngle < 340)
            {
                posVec.x *= -1;
            }

            posVec = posVec.RotatedBy(rotation.eulerAngles.y);

            var matrix = new Matrix4x4();
            matrix.SetTRS(position + posVec, rotation, scale);

            Graphics.DrawMesh(mesh, matrix, mat, layer);
        }

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            codes[codes.Count - 2].operand =
                AccessTools.Method(typeof(Harmony_PawnRenderer_DrawEquipmentAiming), nameof(DrawMeshModified));
            codes.InsertRange(codes.Count - 2, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldarg_3)
            });

            return codes;
        }

        internal static void Prefix(PawnRenderer __instance, ref Vector3 drawLoc)
        {
            var pawn = (Pawn)AccessTools.Field(typeof(PawnRenderer), "pawn").GetValue(__instance);
            if (pawn.Rotation == Rot4.South)
            {
                drawLoc.y++;
            }
        }
    }

    ///// <summary>
    ///// Patches renderer to skip separate drawing of shell layer.
    ///// That layer is now drawn via Harmony_PawnGraphicSet.RenderShellAsNormalLayer, so that it obeys drawOrder.
    ///// </summary>
    //[HarmonyPatch(typeof(PawnRenderer), "RenderPawnInternal")]
    //[HarmonyPatch(new Type[] { typeof(Vector3), typeof(float), typeof(bool), typeof(Rot4), typeof(Rot4), typeof(RotDrawMode), typeof(bool), typeof(bool) })]
    //public static class SkipShellLayerDrawing
    //{
    //    enum PatchStage { Searching, ExtractTargetLabel, PurgingInstructions, Finished };

    //    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    //    {
    //        PatchStage currentStage = PatchStage.Searching;
    //        Label targetLabel = new Label();

    //        foreach (var code in instructions)
    //        {
    //            if (currentStage == PatchStage.Searching)
    //            {
    //                if (code.operand == AccessTools.Field(typeof(ApparelLayerDefOf), "Shell"))
    //                {
    //                    currentStage = PatchStage.ExtractTargetLabel;
    //                }
    //            }
    //            else if (currentStage == PatchStage.ExtractTargetLabel)
    //            {
    //                targetLabel = (Label)code.operand;
    //                currentStage = PatchStage.PurgingInstructions;
    //            }
    //            else if (currentStage == PatchStage.PurgingInstructions)
    //            {
    //                if (code.labels.Contains(targetLabel))
    //                {
    //                    currentStage = PatchStage.Finished;
    //                }
    //                else
    //                {
    //                    code.opcode = OpCodes.Nop;
    //                }
    //            }

    //            yield return code;
    //        }
    //    }
    //}

}