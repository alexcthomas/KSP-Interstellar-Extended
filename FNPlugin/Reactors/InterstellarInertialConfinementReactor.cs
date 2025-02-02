﻿using FNPlugin.Extensions;
using FNPlugin.Resources;
using KSP.Localization;
using System;

namespace FNPlugin.Reactors
{
    [KSPModule("Particle Accelerator")]
    class FNParticleAccelerator : InterstellarInertialConfinementReactor { }

    [KSPModule("Quantum Singularity Reactor")]
    class QuantumSingularityReactor : InterstellarInertialConfinementReactor { }

    [KSPModule("Confinement Fusion Reactor")]
    class IntegratedInertialConfinementReactor : InterstellarInertialConfinementReactor {}

    [KSPModule("Confinement Fusion Engine")]
    class IntegratedInertialConfinementEngine : InterstellarInertialConfinementReactor { }

    [KSPModule("Confinement Fusion Reactor")]
    class InertialConfinementReactor : InterstellarInertialConfinementReactor { }

    [KSPModule("Inertial Confinement Fusion Reactor")]
    class InterstellarInertialConfinementReactor : InterstellarFusionReactor
    {
        // Configs
        [KSPField] public string primaryInputResource = ResourceSettings.Config.ElectricPowerInMegawatt;
        [KSPField] public string secondaryInputResource = ResourceSettings.Config.ElectricPowerInKilowatt;
        [KSPField] public double primaryInputMultiplier = 1;
        [KSPField] public double secondaryInputMultiplier = 1000;
        [KSPField] public bool canJumpstart = true;
        [KSPField] public bool usePowerManagerForPrimaryInputPower = true;
        [KSPField] public bool usePowerManagerForSecondaryInputPower = true;
        [KSPField] public bool canChargeJumpstart = true;
        [KSPField] public float startupPowerMultiplier = 1;
        [KSPField] public float startupCostGravityMultiplier = 0;
        [KSPField] public float startupCostGravityExponent = 1;
        [KSPField] public float startupMaximumGeforce = 10000;
        [KSPField] public float startupMinimumChargePercentage = 0;
        [KSPField] public double geeForceMaintenancePowerMultiplier = 0;
        [KSPField] public bool showSecondaryPowerUsage = false;
        [KSPField] public double gravityDivider;

        // Persistent
        [KSPField(isPersistant = true)]
        public double accumulatedElectricChargeInMW;
        [KSPField(groupName = Group, groupDisplayName = GroupTitle, isPersistant = true, guiActive = true, guiName = "#LOC_KSPIE_InertialConfinementReactor_MaxSecondaryPowerUsage"), UI_FloatRange(stepIncrement = 1f / 3f, maxValue = 100, minValue = 1)]//Max Secondary Power Usage
        public float maxSecondaryPowerUsage = 90;
        [KSPField(groupName = Group, guiName = "#LOC_KSPIE_InertialConfinementReactor_PowerAffectsMaintenance")]//Power Affects Maintenance
        public bool powerControlAffectsMaintenance = true;

        // UI Display
        [KSPField(groupName = Group, guiActive = false, guiUnits = "%", guiName = "#LOC_KSPIE_InertialConfinementReactor_MinimumThrotle", guiFormat = "F2")]//Minimum Throtle
        public double minimumThrottlePercentage;
        [KSPField(groupName = Group, groupDisplayName = GroupTitle, guiActive = true, guiName = "#LOC_KSPIE_InertialConfinementReactor_Charge")]//Charge
        public string accumulatedChargeStr = string.Empty;
        [KSPField(groupName = Group, guiActive = false, guiName = "#LOC_KSPIE_InertialConfinementReactor_FusionPowerRequirement", guiFormat = "F2")]//Fusion Power Requirement
        public double currentLaserPowerRequirements = 0;
        [KSPField(groupName = Group, isPersistant = true, guiName = "#LOC_KSPIE_InertialConfinementReactor_Startup"), UI_Toggle(disabledText = "#LOC_KSPIE_InertialConfinementReactor_Startup_Off", enabledText = "#LOC_KSPIE_InertialConfinementReactor_Startup_Charging")]//Startup--Off--Charging
        public bool isChargingForJumpstart;

        private double _powerConsumed;
        private int jumpStartPowerTime;
        private double _framesPlasmaRatioIsGood;

        private BaseField isChargingField;
        private BaseField accumulatedChargeStrField;
        private PartResourceDefinition primaryInputResourceDefinition;
        private PartResourceDefinition secondaryInputResourceDefinition;

        public override double PlasmaModifier => plasma_ratio;
        public double GravityDivider => startupCostGravityMultiplier * Math.Pow(FlightGlobals.getGeeForceAtPosition(vessel.GetWorldPos3D()).magnitude, startupCostGravityExponent);

        public override void OnStart(PartModule.StartState state)
        {
            isChargingField = Fields[nameof(isChargingForJumpstart)];
            accumulatedChargeStrField = Fields[nameof(accumulatedChargeStr)];

            Fields[nameof(maxSecondaryPowerUsage)].guiActive = showSecondaryPowerUsage;
            Fields[nameof(maxSecondaryPowerUsage)].guiActiveEditor = showSecondaryPowerUsage;

            isChargingField.guiActiveEditor = false;

            base.OnStart(state);

            if (state != StartState.Editor && allowJumpStart)
            {
                if (startDisabled)
                {
                    allowJumpStart = false;
                    IsEnabled = false;
                }
                else
                {
                    jumpStartPowerTime = 50;
                    IsEnabled = true;
                    reactor_power_ratio = 1;
                }

                UnityEngine.Debug.LogWarning("[KSPI]: InterstellarInertialConfinementReactor.OnStart allowJumpStart");
            }

            primaryInputResourceDefinition = !string.IsNullOrEmpty(primaryInputResource)
                ? PartResourceLibrary.Instance.GetDefinition(primaryInputResource)
                : null;

            secondaryInputResourceDefinition = !string.IsNullOrEmpty(secondaryInputResource)
                ? PartResourceLibrary.Instance.GetDefinition(secondaryInputResource)
                : null;
        }

        public override void StartReactor()
        {
            // instead of starting the reactor right away, we always first have to charge it
            isChargingForJumpstart = true;
        }

        public override double MinimumThrottle
        {
            get
            {
                var currentMinimumThrottle = powerPercentage > 0 && base.MinimumThrottle > 0
                    ? Math.Min(base.MinimumThrottle / PowerRatio, 1)
                    : base.MinimumThrottle;

                minimumThrottlePercentage = currentMinimumThrottle * 100;

                return currentMinimumThrottle;
            }
        }

        public double LaserPowerRequirements
        {
            get
            {
                currentLaserPowerRequirements =
                    CurrentFuelMode == null
                    ? PowerRequirement
                    : powerControlAffectsMaintenance
                        ? PowerRatio * NormalizedPowerRequirement
                        : NormalizedPowerRequirement;

                if (geeForceMaintenancePowerMultiplier > 0)
                    currentLaserPowerRequirements += Math.Abs(currentLaserPowerRequirements * geeForceMaintenancePowerMultiplier * part.vessel.geeForce);

                return currentLaserPowerRequirements * primaryInputMultiplier;
            }
        }

        public double StartupPower
        {
            get
            {
                var startupPower = startupPowerMultiplier * LaserPowerRequirements;

                if (!(startupCostGravityMultiplier > 0)) return startupPower;

                gravityDivider = GravityDivider;
                startupPower = gravityDivider > 0 ? startupPower / gravityDivider : startupPower;

                return startupPower;
            }
        }

        public override bool shouldScaleDownJetISP()
        {
            return !isupgraded;
        }

        public override void Update()
        {
            base.Update();

            isChargingField.guiActive = !IsEnabled && HighLogic.LoadedSceneIsFlight && canChargeJumpstart && part.vessel.geeForce < startupMaximumGeforce;
            isChargingField.guiActiveEditor = false;
        }

        public override void OnUpdate()
        {
            if (isChargingField.guiActive)
                accumulatedChargeStr = PluginHelper.GetFormattedPowerString(accumulatedElectricChargeInMW) + " / " + PluginHelper.GetFormattedPowerString(StartupPower);
            else if (part.vessel.geeForce > startupMaximumGeforce)
                accumulatedChargeStr = part.vessel.geeForce.ToString("F2") + "g > " + startupMaximumGeforce.ToString("F2") + "g";
            else
                accumulatedChargeStr = string.Empty;

            accumulatedChargeStrField.guiActive = plasma_ratio < 1;

            electricPowerMaintenance = PluginHelper.GetFormattedPowerString(_powerConsumed) + " / " + PluginHelper.GetFormattedPowerString(LaserPowerRequirements);

            if (startupAnimation != null && !initialized)
            {
                if (IsEnabled)
                {
                    if (animationStarted == 0)
                    {
                        startupAnimation.ToggleAction(new KSPActionParam(KSPActionGroup.Custom01, KSPActionType.Activate));
                        animationStarted = Planetarium.GetUniversalTime();
                    }
                    else if (!startupAnimation.IsMoving())
                    {
                        startupAnimation.ToggleAction(new KSPActionParam(KSPActionGroup.Custom01, KSPActionType.Deactivate));
                        animationStarted = 0;
                        initialized = true;
                        isDeployed = true;
                    }
                }
                else // Not Enabled
                {
                    // continuously start
                    startupAnimation.ToggleAction(new KSPActionParam(KSPActionGroup.Custom01, KSPActionType.Activate));
                    startupAnimation.ToggleAction(new KSPActionParam(KSPActionGroup.Custom01, KSPActionType.Deactivate));
                }
            }
            else if (startupAnimation == null)
            {
                isDeployed = true;
            }

            // call base class
            base.OnUpdate();
        }

        public override void OnFixedUpdate()
        {
            base.OnFixedUpdate();

            UpdateLoopingAnimation(ongoing_consumption_rate * powerPercentage / 100);

            // quit if no fuel available
            if (stored_fuel_ratio <= 0.01)
            {
                plasma_ratio = 0;
                return;
            }

            // stop processing if no power needed
            var laserPowerRequirements = LaserPowerRequirements;
            if (laserPowerRequirements <= 0)
            {
                plasma_ratio = 1;
                return;
            }

            if (!IsEnabled && !isChargingForJumpstart)
            {
                plasma_ratio = 0;
                _powerConsumed = 0;
                allowJumpStart = false;
                if (accumulatedElectricChargeInMW > 0)
                    accumulatedElectricChargeInMW -= 0.01 * accumulatedElectricChargeInMW;
                return;
            }

            ProcessCharging();

            var powerRequested = laserPowerRequirements * required_reactor_ratio;

            double timeWarpFixedDeltaTime = (double)(decimal)Math.Round(TimeWarp.fixedDeltaTime, 7);

            double primaryPowerReceived;
            if (!CheatOptions.InfiniteElectricity && powerRequested > 0)
            {
                primaryPowerReceived = usePowerManagerForPrimaryInputPower
                    ? ConsumeFnResourcePerSecondBuffered(powerRequested, primaryInputResource, 0.1)
                    : part.RequestResource(primaryInputResourceDefinition.id, powerRequested * timeWarpFixedDeltaTime, ResourceFlowMode.STAGE_PRIORITY_FLOW) / timeWarpFixedDeltaTime;
            }
            else
                primaryPowerReceived = powerRequested;

            if (maintenancePowerWasteheatRatio > 0)
                SupplyFnResourcePerSecond(maintenancePowerWasteheatRatio * primaryPowerReceived, ResourceSettings.Config.WasteHeatInMegawatt);

            // calculate effective primary power ratio
            var powerReceived = primaryPowerReceived;
            var powerRequirementMetRatio = powerRequested > 0 ? powerReceived / powerRequested : 1;

            // retrieve any shortage from secondary buffer
            if (secondaryInputMultiplier > 0 && secondaryInputResourceDefinition != null && !CheatOptions.InfiniteElectricity && IsEnabled && powerRequirementMetRatio < 1)
            {
                double currentSecondaryRatio;
                double currentSecondaryCapacity;
                double currentSecondaryAmount;

                if (usePowerManagerForSecondaryInputPower)
                {
                    currentSecondaryRatio = GetResourceBarRatio(secondaryInputResource);
                    currentSecondaryCapacity = GetTotalResourceCapacity(secondaryInputResource);
                    currentSecondaryAmount = currentSecondaryCapacity * currentSecondaryRatio;
                }
                else
                {
                    part.GetConnectedResourceTotals(secondaryInputResourceDefinition.id, out currentSecondaryAmount, out currentSecondaryCapacity);
                    currentSecondaryRatio = currentSecondaryCapacity > 0 ? currentSecondaryAmount / currentSecondaryCapacity : 0;
                }

                var secondaryPowerMaxRatio = ((double)(decimal)maxSecondaryPowerUsage) / 100d;

                // only use buffer if we have sufficient in storage
                if (currentSecondaryRatio > secondaryPowerMaxRatio)
                {
                    // retrieve megawatt ratio
                    var powerShortage = (1 - powerRequirementMetRatio) * powerRequested;
                    var maxSecondaryConsumption = currentSecondaryAmount - (secondaryPowerMaxRatio * currentSecondaryCapacity);
                    var requestedSecondaryPower = Math.Min(maxSecondaryConsumption, powerShortage * secondaryInputMultiplier * timeWarpFixedDeltaTime);
                    var secondaryPowerReceived = part.RequestResource(secondaryInputResource, requestedSecondaryPower);
                    powerReceived += secondaryPowerReceived / secondaryInputMultiplier / timeWarpFixedDeltaTime;
                    powerRequirementMetRatio = powerRequested > 0 ? powerReceived / powerRequested : 1;
                }
            }

            // adjust power to optimal power
            _powerConsumed = laserPowerRequirements * powerRequirementMetRatio;

            // verify if we need startup with accumulated power
            if (canJumpstart && timeWarpFixedDeltaTime <= 0.1 && accumulatedElectricChargeInMW > 0 && _powerConsumed < StartupPower && (accumulatedElectricChargeInMW + _powerConsumed) >= StartupPower)
            {
                var shortage = StartupPower - _powerConsumed;
                if (shortage <= accumulatedElectricChargeInMW)
                {
                    //ScreenMessages.PostScreenMessage("Attempting to Jump start", 5.0f, ScreenMessageStyle.LOWER_CENTER);
                    _powerConsumed += accumulatedElectricChargeInMW;
                    accumulatedElectricChargeInMW -= shortage;
                    jumpStartPowerTime = 50;
                }
            }

            if (isSwappingFuelMode)
            {
                plasma_ratio = 1;
                isSwappingFuelMode = false;
            }
            else if (jumpStartPowerTime > 0)
            {
                plasma_ratio = 1;
                jumpStartPowerTime--;
            }
            else if (_framesPlasmaRatioIsGood > 0) // maintain reactor
            {
                plasma_ratio = Math.Round(laserPowerRequirements > 0 ? _powerConsumed / laserPowerRequirements : 1, 4);
                allowJumpStart = plasma_ratio >= 1;
            }
            else  // starting reactor
            {
                plasma_ratio = Math.Round(StartupPower > 0 ? _powerConsumed / StartupPower : 1, 4);
                allowJumpStart = plasma_ratio >= 1;
            }

            if (plasma_ratio > 0.999)
            {
                plasma_ratio = 1;
                isChargingForJumpstart = false;
                IsEnabled = true;
                if (_framesPlasmaRatioIsGood < 100)
                    _framesPlasmaRatioIsGood += 1;
                if (_framesPlasmaRatioIsGood > 10)
                    accumulatedElectricChargeInMW = 0;
            }
            else
            {
                var threshold = 10 * (1 - plasma_ratio);
                if (_framesPlasmaRatioIsGood >= threshold)
                {
                    _framesPlasmaRatioIsGood -= threshold;
                    plasma_ratio = 1;
                }
            }
        }

        private void UpdateLoopingAnimation(double ratio)
        {
            if (loopingAnimation == null)
                return;

            if (!isDeployed)
                return;

            if (!IsEnabled)
            {
                if (!initialized || shutdownAnimation == null || loopingAnimation.IsMoving()) return;

                if (!(animationStarted >= 0))
                {
                    animationStarted = Planetarium.GetUniversalTime();
                    shutdownAnimation.ToggleAction(new KSPActionParam(KSPActionGroup.Custom01, KSPActionType.Activate));
                }
                else if (!shutdownAnimation.IsMoving())
                {
                    shutdownAnimation.ToggleAction(new KSPActionParam(KSPActionGroup.Custom01, KSPActionType.Deactivate));
                    initialized = false;
                    isDeployed = true;
                }
                return;
            }

            if (!loopingAnimation.IsMoving())
                loopingAnimation.Toggle();
        }

        private void ProcessCharging()
        {
            double timeWarpFixedDeltaTime = TimeWarp.fixedDeltaTime;
            if (!canJumpstart || !isChargingForJumpstart || !(part.vessel.geeForce < startupMaximumGeforce)) return;

            var neededPower = Math.Max(StartupPower - accumulatedElectricChargeInMW, 0);

            if (neededPower <= 0)
                return;

            var availableStablePower = GetStableResourceSupply(ResourceSettings.Config.ElectricPowerInMegawatt);

            var minimumChargingPower = startupMinimumChargePercentage * RawPowerOutput;
            if (startupCostGravityMultiplier > 0)
            {
                gravityDivider = GravityDivider;
                minimumChargingPower = gravityDivider > 0 ? minimumChargingPower / gravityDivider : minimumChargingPower;
            }

            if (availableStablePower < minimumChargingPower)
            {
                if (startupCostGravityMultiplier > 0)
                    ScreenMessages.PostScreenMessage(Localizer.Format("#LOC_KSPIE_InertialConfinementReactor_PostMsg1", minimumChargingPower.ToString("F0")), 1f, ScreenMessageStyle.UPPER_CENTER);//"Curent you need at least " +  + " MW to charge the reactor. Move closer to gravity well to reduce amount needed"
                else
                    ScreenMessages.PostScreenMessage(Localizer.Format("#LOC_KSPIE_InertialConfinementReactor_PostMsg2", minimumChargingPower.ToString("F0")), 5f, ScreenMessageStyle.UPPER_CENTER);//"You need at least " +  + " MW to charge the reactor"
            }
            else
            {
                var megaJouleRatio = usePowerManagerForPrimaryInputPower
                    ? GetResourceBarRatio(primaryInputResource)
                    : part.GetResourceRatio(primaryInputResource);

                var primaryPowerRequest = Math.Min(neededPower, availableStablePower * megaJouleRatio);

                // verify we amount of power collected exceeds threshold
                var returnedPrimaryPower = CheatOptions.InfiniteElectricity
                    ? neededPower
                    : usePowerManagerForPrimaryInputPower
                        ? ConsumeFnResourcePerSecond(primaryPowerRequest, primaryInputResource)
                        : part.RequestResource(primaryInputResource, primaryPowerRequest * timeWarpFixedDeltaTime);

                var powerPerSecond = usePowerManagerForPrimaryInputPower ? returnedPrimaryPower : returnedPrimaryPower / timeWarpFixedDeltaTime;

                if (!CheatOptions.IgnoreMaxTemperature && maintenancePowerWasteheatRatio > 0)
                    SupplyFnResourcePerSecond(0.05 * powerPerSecond, ResourceSettings.Config.WasteHeatInMegawatt);

                if (powerPerSecond >= minimumChargingPower)
                    accumulatedElectricChargeInMW += returnedPrimaryPower * timeWarpFixedDeltaTime;
                else
                {
                    if (startupCostGravityMultiplier > 0)
                        ScreenMessages.PostScreenMessage(Localizer.Format("#LOC_KSPIE_InertialConfinementReactor_PostMsg1", minimumChargingPower.ToString("F0")), 5f, ScreenMessageStyle.UPPER_CENTER);//"Curent you need at least " +  + " MW to charge the reactor. Move closer to gravity well to reduce amount needed"
                    else
                        ScreenMessages.PostScreenMessage(Localizer.Format("#LOC_KSPIE_InertialConfinementReactor_PostMsg2", minimumChargingPower.ToString("F0")), 5f, ScreenMessageStyle.UPPER_CENTER);//"You need at least " +  + " MW to charge the reactor"
                }
            }

            // secondary try to charge from secondary Power Storage
            neededPower = StartupPower - accumulatedElectricChargeInMW;
            if (secondaryInputMultiplier > 0 && neededPower > 0 && startupMinimumChargePercentage <= 0)
            {
                var requestedSecondaryPower = neededPower * secondaryInputMultiplier;

                var secondaryPowerReceived = usePowerManagerForSecondaryInputPower
                    ? consumeFNResource(requestedSecondaryPower, secondaryInputResource)
                    : part.RequestResource(secondaryInputResource, requestedSecondaryPower);

                accumulatedElectricChargeInMW += secondaryPowerReceived / secondaryInputMultiplier;
            }
        }

        public override void UpdateEditorPowerOutput()
        {
            base.UpdateEditorPowerOutput();
            electricPowerMaintenance = PluginHelper.GetFormattedPowerString(LaserPowerRequirements) + " / " + PluginHelper.GetFormattedPowerString(LaserPowerRequirements);
        }
    }
}
