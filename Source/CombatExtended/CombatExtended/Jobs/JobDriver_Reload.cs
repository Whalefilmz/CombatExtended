﻿using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace CombatExtended
{
    public class JobDriver_Reload : JobDriver
    {
        #region Fields
        private CompAmmoUser _compReloader;
        private bool inEquipment = false;
        private bool inInventory = false;
        private ThingWithComps initEquipment = null;
        #endregion Fields

        #region Properties
        private string errorBase => this.GetType().Assembly.GetName().Name + " :: " + this.GetType().Name + " :: ";

        private CompAmmoUser compReloader
        {
            get
            {
                if (_compReloader == null) _compReloader = TargetThingB.TryGetComp<CompAmmoUser>();
                return _compReloader;
            }
        }
        #endregion Properties

        #region Methods
        public override string GetReport()
        {
            string text = CE_JobDefOf.ReloadWeapon.reportString;
            string flagSource = "";
            if (inEquipment) flagSource = "CE_ReloadingEquipment".Translate();
            if (inInventory) flagSource = "CE_ReloadingInventory".Translate();
            text = text.Replace("FlagSource", flagSource);
            text = text.Replace("TargetB", TargetThingB.def.label);
            text = text.Replace("AmmoType", compReloader.currentAmmo.label);
            return text;
        }

        private bool HasNoGunOrAmmo()
        {
            //if (TargetThingB.DestroyedOrNull() || pawn.equipment == null || pawn.equipment.Primary == null || pawn.equipment.Primary != TargetThingB)
			if (TargetThingB.DestroyedOrNull() || (inEquipment && (pawn?.equipment?.Primary == null || pawn.equipment.Primary != TargetThingB))
			   								   || (inInventory && (pawn.inventory == null || !pawn.inventory.innerContainer.Contains(TargetThingB)))
			                                   || (initEquipment != pawn?.equipment?.Primary))
                return true;

			//CompAmmoUser comp = pawn.equipment.Primary.TryGetComp<CompAmmoUser>();
			//return comp != null && comp.useAmmo && !comp.hasAmmo;
			//return comp != null && !comp.hasAndUsesAmmoOrMagazine;
			// expecting true ends the job.  if comp == null then will return false from the first part and not test the second.  Job will continue (bad).

			return compReloader == null || !compReloader.hasAndUsesAmmoOrMagazine;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            if (compReloader == null)
            {
                Log.Error(pawn + " tried to do reload job without compReloader");
                yield return null;
            }
            if (compReloader.holder == null)
            {
                Log.Error("JobDriver_Reload :: compReloader.holder is null.  Weapon to be reloaded must be Pawn's Primary equipment or in Pawn's inventory.");
                yield return null;
            }
            
            // initial location of the parent item.
            if (compReloader.wielder != null) inEquipment = true;
			else if (compReloader.holder != null) inInventory = true;
			else
				this.EndJobWith(JobCondition.Incompletable); // can't do the job if what we are reloading isn't in inventory or equipment.
            
            // current state of equipment, want to interrupt the reload if a pawn's equipment changes.
            initEquipment = pawn.equipment?.Primary;

            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOnMentalState(TargetIndex.A);
            this.FailOn(HasNoGunOrAmmo);

            // Throw mote
            if (compReloader.Props.throwMote)
                MoteMaker.ThrowText(pawn.Position.ToVector3Shifted(), Find.VisibleMap, string.Format("CE_ReloadingMote".Translate(), TargetThingB.def.LabelCap));

            //Toil of do-nothing		
            Toil waitToil = new Toil() { actor = pawn }; // actor was always null in testing...
            waitToil.initAction = () => waitToil.actor.pather.StopDead();
            waitToil.defaultCompleteMode = ToilCompleteMode.Delay;
            waitToil.defaultDuration = Mathf.CeilToInt(compReloader.Props.reloadTicks / pawn.GetStatValue(CE_StatDefOf.ReloadSpeed));
            yield return waitToil.WithProgressBarToilDelay(TargetIndex.A);

            //Actual reloader
            Toil reloadToil = new Toil();
            reloadToil.AddFinishAction(() => compReloader.LoadAmmo());
            yield return reloadToil;

            //Continue previous job if possible
            Toil continueToil = new Toil
            {
                initAction = () => compReloader.TryContinuePreviousJob(),
                defaultCompleteMode = ToilCompleteMode.Instant
            };
            yield return continueToil;
        }
        #endregion Methods
    }
}