using System;
using System.Collections.Generic;
using System.Text;
using VRage.Game.Components;
using Sandbox.ModAPI;
using VRage.Utils;
using Sandbox.Game.Entities;
using IMyControllableEntity = VRage.Game.ModAPI.Interfaces.IMyControllableEntity;
using VRage.Game.ModAPI;
using VRageMath;
using VRage.Game;

namespace SE_Booster
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class ThrustBoost : MySessionComponentBase
    {
        private static readonly Vector4 BoosterFlameColor = new Vector4(1f, 0.4f, 0f, 1f);

        /// <summary>
        ///     Thrust boost level
        /// </summary>
        private const float BOOST_THRUST = 5f;

        /// <summary>
        ///     POwer usage boost level
        /// </summary>
        private const float BOOST_POWER = 2.5f;

        /// <summary>
        ///     Strength boost level
        /// </summary>
        private const float BOOST_STRENGTH = 2.5f;

        /// <summary>
        ///     Max heat level
        /// </summary>
        private const float MAX_HEAT = 100f;

        /// <summary>
        ///     Max boost time (sec)
        /// </summary>
        private const float MAX_BOOST_TIME = 10f;

        /// <summary>
        ///     Heat buildup and dissipation step
        /// </summary>
        private const float HEAT_QUANT = MAX_HEAT / 60f / MAX_BOOST_TIME;

        private bool _initialized;
        private bool _hintShown;
        private IMyCubeGrid _grid;
        private IMyHudNotification _notification;
        private readonly List<IMySlimBlock> _slimBlocks = new List<IMySlimBlock>();
        private readonly List<MyThrust> _thrusters = new List<MyThrust>();

        private void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            MyLog.Default.Info("Init #{0} \"{1}\"", Session.Player.IdentityId, Session.Player.DisplayName);

            Session.Player.Controller.ControlledEntityChanged += ControlledEntityChanged;
            ControlledEntityChanged(null, Session.Player.Controller.ControlledEntity);

            _notification = MyAPIGateway.Utilities.CreateNotification("");
            _notification.Font = "Monospace";

            _initialized = true;
        }

        public override void UpdateBeforeSimulation()
        {
            base.UpdateBeforeSimulation();
            Initialize();

            if (_grid == null || _thrusters.Count == 0)
            {
                return;
            }

            var hasOverheat = false;
            foreach (var t in _thrusters)
            {
                ThrusterMode thrusterMode;
                ReadThrusterState(t, out thrusterMode);
                hasOverheat = hasOverheat || thrusterMode == ThrusterMode.Overheat;
            }

            var enableBoost = MyAPIGateway.Input.IsAnyShiftKeyPressed() && !hasOverheat;

            var heatLevel = 0f;
            hasOverheat = false;
            foreach (var t in _thrusters)
            {
                float thrusterHeat;
                ThrusterMode thrusterMode;
                UpdateThruster(t, enableBoost, out thrusterHeat, out thrusterMode);
                if (heatLevel < thrusterHeat)
                {
                    heatLevel = thrusterHeat;
                }

                hasOverheat = hasOverheat || thrusterMode == ThrusterMode.Overheat;
            }

            if (enableBoost || heatLevel > 0f)
            {
                var progress = 100f * heatLevel / MAX_HEAT;
                _notification.Text = RenderMessage(progress, hasOverheat, enableBoost);

                _notification.ResetAliveTime();
                _notification.Show();
            }
            else
            {
                _notification.Hide();
            }
        }

        private static readonly StringBuilder SB = new StringBuilder();

        private static string RenderMessage(float progress, bool hasOverheat, bool boostersEngaged)
        {
            const int progressBarWidth = 40;

            var fill = (int)Math.Ceiling(progress * progressBarWidth / 100f);

            SB.Clear();
            if (!hasOverheat)
            {
                if (boostersEngaged)
                {
                    SB.AppendLine("<<<          BOOSTERS ENGAGED          >>>");
                }
                else
                {
                    SB.AppendLine("<<<          BOOSTERS COOLDOWN         >>>");
                }
            }
            else
            {
                SB.AppendLine("<<<          BOOSTERS OVERHEAT         >>>");
            }

            SB.Append("[");
            var i = 0;
            for (; i < fill; i++)
            {
                SB.Append('#');
            }
            for (; i < progressBarWidth; i++)
            {
                SB.Append('_');
            }
            SB.Append("]");

            return SB.ToString();
        }

        private void ControlledEntityChanged(IMyControllableEntity _, IMyControllableEntity entity)
        {
            if (_grid != null)
            {
                _grid.OnBlockAdded -= OnBlockAdded;
                _grid.OnBlockRemoved -= OnBlockRemoved;
            }

            _grid = (entity?.Entity as IMyCockpit)?.CubeGrid;
            if (_grid == null)
            {
                MyLog.Default.Info("Player #{0} {1} left ship", Session.Player.IdentityId, Session.Player.DisplayName);
                _slimBlocks.Clear();
                _thrusters.Clear();
                _boostInfo.Clear();
                return;
            }

            UpdateThrusters();

            _grid.OnBlockAdded += OnBlockAdded;
            _grid.OnBlockRemoved += OnBlockRemoved;

            MyLog.Default.Info(
                "Player #{0} {1} entered ship #{2} ({3} thrusters)",
                Session.Player.IdentityId,
                Session.Player.DisplayName,
                _grid.EntityId,
                _thrusters.Count);

            if (!_hintShown)
            {
                MyAPIGateway.Utilities.ShowNotification("Hold <Shift> to activate thrust boosters", 5000, MyFontEnum.UrlHighlight);
                _hintShown = true;
            }
        }

        private void OnBlockAdded(IMySlimBlock _)
        {
            UpdateThrusters();
        }

        private void OnBlockRemoved(IMySlimBlock _)
        {
            UpdateThrusters();
        }

        private void UpdateThrusters()
        {
            _slimBlocks.Clear();
            _thrusters.Clear();

            _grid.GetBlocks(_slimBlocks, block => block.FatBlock is MyThrust);
            _thrusters.Capacity = _slimBlocks.Count;
            for (var i = 0; i < _slimBlocks.Count; i++)
            {
                _thrusters.Add((MyThrust)_slimBlocks[i].FatBlock);
            }
        }

        private readonly Dictionary<long, ThrusterBoostInfo> _boostInfo = new Dictionary<long, ThrusterBoostInfo>();

        private enum ThrusterMode
        {
            Normal,
            Boost,
            Overheat
        }

        struct ThrusterBoostInfo
        {
            public float CurrentHeatLevel;
            public Vector4 OriginalColor;
            public ThrusterMode Mode;
        }

        private void ReadThrusterState(MyThrust thruster, out ThrusterMode mode)
        {
            ThrusterBoostInfo boostInfo;
            if (!_boostInfo.TryGetValue(thruster.EntityId, out boostInfo))
            {
                boostInfo = new ThrusterBoostInfo();
            }

            mode = boostInfo.Mode;
        }

        private void UpdateThruster(MyThrust thruster, bool enableBoost, out float heatLevel, out ThrusterMode mode)
        {
            ThrusterBoostInfo boostInfo;
            if (!_boostInfo.TryGetValue(thruster.EntityId, out boostInfo))
            {
                boostInfo = new ThrusterBoostInfo();
            }

            switch (boostInfo.Mode)
            {
                case ThrusterMode.Boost:
                    UpdateThrusterBoostMode(thruster, enableBoost, ref boostInfo);
                    break;
                case ThrusterMode.Overheat:
                    UpdateThrusterOverheatMode(thruster, enableBoost, ref boostInfo);
                    break;
                default:
                    UpdateThrusterNormalMode(thruster, enableBoost, ref boostInfo);
                    break;
            }

            _boostInfo[thruster.EntityId] = boostInfo;
            heatLevel = boostInfo.CurrentHeatLevel;
            mode = boostInfo.Mode;
        }

        private void UpdateThrusterNormalMode(MyThrust thruster, bool enableBoost, ref ThrusterBoostInfo boostInfo)
        {
            if (enableBoost)
            {
                EnableBooster(thruster, ref boostInfo);
                boostInfo.Mode = ThrusterMode.Boost;
            }
            else
            {
                ApplyCooldown(thruster, ref boostInfo);
            }
        }

        private void UpdateThrusterBoostMode(MyThrust thruster, bool enableBoost, ref ThrusterBoostInfo boostInfo)
        {
            if (enableBoost)
            {
                var overheat = ApplyHeatup(thruster, ref boostInfo);
                if (overheat)
                {
                    DisableBooster(thruster, ref boostInfo);
                    boostInfo.Mode = ThrusterMode.Overheat;

                    thruster.SetEffect("Damage", true);
                }
            }
            else
            {
                DisableBooster(thruster, ref boostInfo);
                boostInfo.Mode = ThrusterMode.Normal;
            }
        }

        private void UpdateThrusterOverheatMode(MyThrust thruster, bool enableBoost, ref ThrusterBoostInfo boostInfo)
        {
            ApplyCooldown(thruster, ref boostInfo);

            if (boostInfo.CurrentHeatLevel <= 0f)
            {
                thruster.RemoveEffect("Damage");

                if (enableBoost)
                {
                    EnableBooster(thruster, ref boostInfo);
                    boostInfo.Mode = ThrusterMode.Boost;
                }
                else
                {
                    boostInfo.Mode = ThrusterMode.Normal;
                }
            }
        }

        private bool ApplyHeatup(MyThrust thruster, ref ThrusterBoostInfo boostInfo)
        {
            var t = (IMyThrust)thruster;

            var thrustLevel = t.CurrentThrust / t.MaxThrust;
            var heatQuant = thrustLevel * HEAT_QUANT;
            boostInfo.CurrentHeatLevel += heatQuant;

            if (boostInfo.CurrentHeatLevel >= MAX_HEAT)
            {
                boostInfo.CurrentHeatLevel = MAX_HEAT;
                DisableBooster(thruster, ref boostInfo);
                return true;
            }

            return false;
        }

        private void ApplyCooldown(MyThrust thruster, ref ThrusterBoostInfo boostInfo)
        {
            var t = (IMyThrust)thruster;

            var thrustLevel = t.CurrentThrust / t.MaxThrust;
            var cooldownQuant = (1f - thrustLevel * 0.75f) * HEAT_QUANT;
            boostInfo.CurrentHeatLevel -= cooldownQuant;

            if (boostInfo.CurrentHeatLevel < 0)
            {
                boostInfo.CurrentHeatLevel = 0f;
            }
        }

        private void EnableBooster(MyThrust thruster, ref ThrusterBoostInfo boostInfo)
        {
            var t = (IMyThrust)thruster;

            t.ThrustMultiplier = BOOST_THRUST;
            t.PowerConsumptionMultiplier = BOOST_POWER;

            boostInfo.OriginalColor = thruster.ThrustColor;
            thruster.Light.Color = BoosterFlameColor;
            thruster.UpdateLight();

            thruster.CurrentStrength = BOOST_STRENGTH;
            thruster.UpdateThrustFlame();
        }

        private void DisableBooster(MyThrust thruster, ref ThrusterBoostInfo boostInfo)
        {
            var t = (IMyThrust)thruster;

            t.ThrustMultiplier = 1f;
            t.PowerConsumptionMultiplier = 1f;

            thruster.Light.Color = boostInfo.OriginalColor;
            thruster.UpdateLight();

            thruster.CurrentStrength = 1f;
            thruster.UpdateThrustFlame();
        }
    }
}
