using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using System.Xml.Linq;

using Demo3D.Common;
using Demo3D.Utilities;
using Demo3D.PLC;
using Demo3D.PLC.Comms;
using Demo3D.Gui.AspectViewer;
using Demo3D.Gui.AspectViewer.Editors;
using Demo3D.Visuals;
using Demo3D.Visuals.Motor;

using KJEUtil = Demo3D.Visuals.KJE.Utilities;
using Demo3D.Components.Properties;

namespace Demo3D.Components
{
    [Resources(typeof(Resources))]
    [Category(nameof(Resources.Motor_Controllers_Category))]
    [HelpUrl("advanced_actuation_controller")]
    public sealed class AdvancedActuationController : ExportableVisualAspect, IController, IBindableItemOwner
    {
        public enum ControlModes
        {
            Activate,
            ExtendRetract
        }

        public enum TargetType
        {
            Discontinuous,
            Continuous
        }

        [Flags]
        public enum ResettableProperties
        {
            None = 0,
            Speed = 1 << 0,
            Acceleration = 1 << 1,
            Deceleration = 1 << 2,
            State = 1 << 3
        }

        [Flags]
        public enum Input
        {
            None = 0,
            Speed = 1 << 0,
            Acceleration = 1 << 1,
            Deceleration = 1 << 2,
            State = 1 << 3
        }

        [Flags]
        public enum Output
        {
            None = 0,
            AtExtended = 1 << 0,
            AtRetracted = 1 << 1,
            IsMoving = 1 << 2
        }

        private MotorAspect motor;

        private ControlModes controlMode;
        private bool swapPositions;
        private bool activate;
        private bool isActivateVisible;
        private bool extend;
        private bool isExtendVisible;
        private bool retract;
        private bool isRetractVisible;

        private BindableItem<bool> activateBindableItem;
        private BindableItem<bool> extendBindableItem;
        private BindableItem<bool> retractBindableItem;

        private Input inputs;
        private Output outputs;

        private TargetType targetType;

        private double speed;
        private double resetSpeed;
        private double acceleration;
        private double resetAcceleration;
        private double deceleration;
        private double resetDeceleration;
        private bool state;
        private bool resetState;

        private bool atExtended;
        private bool atRetracted;
        private bool isMoving;

        private ResettableProperties resetProperties;

        private BindableItem<double> speedBindableItem;
        private BindableItem<double> accelerationBindableItem;
        private BindableItem<double> decelerationBindableItem;
        private BindableItem<bool> stateBindableItem;

        private BindableItem<bool> atExtendedBindableItem;
        private BindableItem<bool> atRetractedBindableItem;
        private BindableItem<bool> isMovingBindableItem;

        private MotorAspect.Lock<MotorState> stateLock;
        private MotorAspect.Lock<MotorDirection> directionLock;
        private MotorAspect.Lock<double> speedLock;
        private MotorAspect.Lock<double> accelerationLock;
        private MotorAspect.Lock<double> decelerationLock;

        private MotionPrompt prompt;

        [AspectProperty(IsVisible = true), Category("General"), Description("Name of the controller (optional)")]
        public override string Name
        {
            get { return base.Name; }
            set { base.Name = value; }
        }

        [AspectProperty, Category("General"), Description("Motor the controller is bound to")]
        public MotorAspect Motor
        {
            get { return motor; }
        }

        [AspectProperty, Category("General")]
        [AspectEditor(IsVisiblePropertyLink = nameof(IsAngular), IsEnabledPropertyLink = nameof(IsAngular))]
        [DefaultValue(TargetType.Discontinuous)]
        public TargetType TargetMode
        {
            get { return targetType; }
            set
            {
                if (SetProperty(ref targetType, value))
                {
                    if (IsInitialized)
                    {
                        UpdatePrompt();
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public UnitsAttribute PositionUnits
        {
            get { return (motor != null) ? motor.PositionUnits : null; }
        }

        [AspectProperty(IsVisible = false)]
        public UnitsAttribute SpeedUnits
        {
            get { return (motor != null) ? motor.SpeedUnits : null; }
        }

        [AspectProperty(IsVisible = false)]
        public UnitsAttribute AccelerationUnits
        {
            get { return (motor != null) ? motor.AccelerationUnits : null; }
        }

        [AspectProperty(IsVisible = false)]
        public bool IsAngular
        {
            get { return PositionUnits is AngleAttribute; }
        }

        [AspectProperty, Category("Configuration"), Description("Activate (1 signal) or extend/retract (2 signals)")]
        [DefaultValue(ControlModes.ExtendRetract)]
        public ControlModes ControlMode
        {
            get { return controlMode; }
            set
            {
                SetProperty(ref controlMode, value);

                IsActivateVisible = controlMode == ControlModes.Activate;
                IsExtendVisible = controlMode == ControlModes.ExtendRetract;
                IsRetractVisible = controlMode == ControlModes.ExtendRetract;

                RaisePropertyChanged(nameof(Activate));
                RaisePropertyChanged(nameof(Extend));
                RaisePropertyChanged(nameof(Retract));

                UpdateBindings();
            }
        }

        [AspectProperty, Category("Configuration"), Description("Swap the motor's extended & retracted positions")]
        [DefaultValue(false)]
        public bool SwapPositions
        {
            get { return swapPositions; }
            set
            {
                SetProperty(ref swapPositions, value);
                RaisePropertyChanged(nameof(SwapPositions));

                if (IsInitialized)
                {
                    UpdatePrompt();
                }
            }
        }

        [AspectProperty, Category("Inputs")]
        [DefaultValue(Input.None)]
        public Input Inputs
        {
            get { return inputs; }
            set
            {
                if (SetProperty(ref inputs, value))
                {
                    if (motor != null && TryLockProperties() == false)
                    {
                        SetProperty(ref inputs, Input.None);
                    }

                    if (!InputState)
                    {
                        // state flag unchecked, reset internal state to true
                        state = true;
                    }

                    UpdateBindings();

                    RaisePropertyChanged(nameof(InputSpeed));
                    RaisePropertyChanged(nameof(InputAcceleration));
                    RaisePropertyChanged(nameof(InputDeceleration));
                    RaisePropertyChanged(nameof(InputState));
                }
            }
        }

        [AspectProperty, Category("Inputs"), Description("Move the motor to its extended position (true) or retracted position (false)")]
        [AspectEditor(IsVisiblePropertyLink = nameof(IsActivateVisible))]
        [DefaultValue(false)]
        public bool Activate
        {
            get { return activate; }
            set
            {
                if (ActivateBindableItem != null)
                {
                    if (ActivateBindableItem.ValueAs<bool>() != value)
                    {
                        ActivateBindableItem.Value = value;
                    }
                }

                if (SetProperty(ref activate, value))
                {
                    if (IsInitialized)
                    {
                        UpdatePrompt();
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<bool> ActivateBindableItem
        {
            get { return activateBindableItem; }
            private set
            {
                if (activateBindableItem != value)
                {
                    var activate = Activate;

                    if (activateBindableItem != null)
                    {
                        activateBindableItem.ValueChanged -= OnActivateBindableItemChanged;
                        activateBindableItem.DetachFromVisual();
                        activateBindableItem = null;
                    }

                    activateBindableItem = value;
                    if (activateBindableItem != null)
                    {
                        activateBindableItem.ValueChanged += OnActivateBindableItemChanged; ;
                        activateBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
                        activateBindableItem.Value = activate;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool IsActivateVisible
        {
            get { return isActivateVisible; }
            set { isActivateVisible = value; }
        }

        [AspectProperty, Category("Inputs"), Description("Move the motor to its extended position")]
        [AspectEditor(IsVisiblePropertyLink = nameof(IsExtendVisible))]
        [DefaultValue(false)]
        public bool Extend
        {
            get { return extend; }
            set
            {
                if (ExtendBindableItem != null)
                {
                    if (ExtendBindableItem.ValueAs<bool>() != value)
                    {
                        ExtendBindableItem.Value = value;
                    }
                }

                if (SetProperty(ref extend, value))
                {
                    if (IsInitialized)
                    {
                        UpdatePrompt();
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<bool> ExtendBindableItem
        {
            get { return extendBindableItem; }
            private set
            {
                if (extendBindableItem != value)
                {
                    var extend = Extend;

                    if (extendBindableItem != null)
                    {
                        extendBindableItem.ValueChanged -= OnExtendBindableItemChanged;
                        extendBindableItem.DetachFromVisual();
                        extendBindableItem = null;
                    }

                    extendBindableItem = value;
                    if (extendBindableItem != null)
                    {
                        extendBindableItem.ValueChanged += OnExtendBindableItemChanged; ;
                        extendBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
                        extendBindableItem.Value = extend;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool IsExtendVisible
        {
            get { return isExtendVisible; }
            set { isExtendVisible = value; }
        }

        [AspectProperty, Category("Inputs"), Description("Move the motor to its retracted position")]
        [AspectEditor(IsVisiblePropertyLink = nameof(IsRetractVisible))]
        [DefaultValue(false)]
        public bool Retract
        {
            get { return retract; }
            set
            {
                if (RetractBindableItem != null)
                {
                    if (RetractBindableItem.ValueAs<bool>() != value)
                    {
                        RetractBindableItem.Value = value;
                    }
                }

                if (SetProperty(ref retract, value))
                {
                    if (IsInitialized)
                    {
                        UpdatePrompt();
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<bool> RetractBindableItem
        {
            get { return retractBindableItem; }
            private set
            {
                if (retractBindableItem != value)
                {
                    var retract = Retract;

                    if (retractBindableItem != null)
                    {
                        retractBindableItem.ValueChanged -= OnRetractBindableItemChanged;
                        retractBindableItem.DetachFromVisual();
                        retractBindableItem = null;
                    }

                    retractBindableItem = value;
                    if (retractBindableItem != null)
                    {
                        retractBindableItem.ValueChanged += OnRetractBindableItemChanged; ;
                        retractBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
                        retractBindableItem.Value = retract;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool IsRetractVisible
        {
            get { return isRetractVisible; }
            set { isRetractVisible = value; }
        }

        [AspectProperty, Category("Inputs"), Description("Target speed of the motor")]
        [DefaultValue(0.0)]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(SpeedUnits), IsVisiblePropertyLink = nameof(InputSpeed))]
        public double Speed
        {
            get { return speed; }
            set
            {
                if (SpeedBindableItem != null)
                {
                    if (SpeedBindableItem.ValueAs<double>() != value)
                    {
                        SpeedBindableItem.Value = value;
                    }
                }

                if (SetProperty(ref speed, value))
                {
                    SetMotorSpeed(speed);
                }
            }
        }

        [AspectProperty, Category("Inputs"), Description("Acceleration of the motor")]
        [DefaultValue(0.0)]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(AccelerationUnits), IsVisiblePropertyLink = nameof(InputAcceleration))]
        public double Acceleration
        {
            get { return acceleration; }
            set
            {
                if (AccelerationBindableItem != null)
                {
                    if (AccelerationBindableItem.ValueAs<double>() != value)
                    {
                        AccelerationBindableItem.Value = value;
                    }
                }

                if (SetProperty(ref acceleration, value))
                {
                    SetMotorAcceleration(acceleration);
                }
            }
        }

        [AspectProperty, Category("Inputs"), Description("Deceleration of the motor")]
        [DefaultValue(0.0)]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(AccelerationUnits), IsVisiblePropertyLink = nameof(InputDeceleration))]
        public double Deceleration
        {
            get { return deceleration; }
            set
            {
                if (DecelerationBindableItem != null)
                {
                    if (DecelerationBindableItem.ValueAs<double>() != value)
                    {
                        DecelerationBindableItem.Value = value;
                    }
                }

                if (SetProperty(ref deceleration, value))
                {
                    SetMotorDeceleration(deceleration);
                }
            }
        }

        [AspectProperty, Category("Inputs"), Description("State of the motor")]
        [AspectEditorAttribute(IsVisiblePropertyLink = nameof(InputState))]
        [DefaultValue(true)]
        public bool State
        {
            get { return state; }
            set
            {
                if (StateBindableItem != null)
                {
                    if (StateBindableItem.ValueAs<bool>() != value)
                    {
                        StateBindableItem.Value = value;
                    }
                }

                if (SetProperty(ref state, value))
                {
                    if (IsInitialized)
                    {
                        SetMotorState(state ? MotorState.On : MotorState.Off);
                        UpdatePrompt();
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool InputSpeed
        {
            get { return inputs.HasFlag(Input.Speed); }
        }

        [AspectProperty(IsVisible = false)]
        public bool InputAcceleration
        {
            get { return inputs.HasFlag(Input.Acceleration); }
        }

        [AspectProperty(IsVisible = false)]
        public bool InputDeceleration
        {
            get { return inputs.HasFlag(Input.Deceleration); }
        }

        [AspectProperty(IsVisible = false)]
        public bool InputState
        {
            get { return inputs.HasFlag(Input.State); }
        }

        [AspectProperty, Category("Outputs")]
        [DefaultValue(Output.None)]
        public Output Outputs
        {
            get { return outputs; }
            set
            {
                if (SetProperty(ref outputs, value))
                {
                    UpdateBindings();

                    RaisePropertyChanged(nameof(OutputAtExtended));
                    RaisePropertyChanged(nameof(AtExtended));
                    RaisePropertyChanged(nameof(OutputAtRetracted));
                    RaisePropertyChanged(nameof(AtRetracted));
                    RaisePropertyChanged(nameof(OutputIsMoving));
                    RaisePropertyChanged(nameof(IsMoving));
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool OutputAtExtended
        {
            get { return outputs.HasFlag(Output.AtExtended); }
        }

        [AspectProperty, Category("Outputs"), Description("Whether the motor is at the extended position")]
        [AspectEditor(IsVisiblePropertyLink = nameof(OutputAtExtended), IsEnabledPropertyLink = nameof(OutputAtExtended))]
        public bool AtExtended
        {
            get { return atExtended; }
            private set
            {
                if (AtExtendedBindableItem != null)
                {
                    if (AtExtendedBindableItem.ValueAs<bool>() != value)
                    {
                        AtExtendedBindableItem.Value = value;
                    }
                }

                SetProperty(ref atExtended, value);
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool OutputAtRetracted
        {
            get { return outputs.HasFlag(Output.AtRetracted); }
        }

        [AspectProperty, Category("Outputs"), Description("Whether the motor is at the retracted position")]
        [AspectEditor(IsVisiblePropertyLink = nameof(OutputAtRetracted), IsEnabledPropertyLink = nameof(OutputAtRetracted))]
        public bool AtRetracted
        {
            get { return atRetracted; }
            private set
            {
                if (AtRetractedBindableItem != null)
                {
                    if (AtRetractedBindableItem.ValueAs<bool>() != value)
                    {
                        AtRetractedBindableItem.Value = value;
                    }
                }

                SetProperty(ref atRetracted, value);
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool OutputIsMoving
        {
            get { return outputs.HasFlag(Output.IsMoving); }
        }

        [AspectProperty, Category("Outputs"), Description("Whether the motor is moving")]
        [AspectEditor(IsVisiblePropertyLink = nameof(OutputIsMoving), IsEnabledPropertyLink = nameof(OutputIsMoving))]
        public bool IsMoving
        {
            get { return isMoving; }
            private set
            {
                if (IsMovingBindableItem != null)
                {
                    if (IsMovingBindableItem.ValueAs<bool>() != value)
                    {
                        IsMovingBindableItem.Value = value;
                    }
                }

                SetProperty(ref isMoving, value);
            }
        }

        [AspectProperty, Category("Reset")]
        [DefaultValue(ResettableProperties.None)]
        public ResettableProperties ResetProperties
        {
            get { return resetProperties; }
            set
            {
                if (SetProperty(ref resetProperties, value))
                {
                    RaisePropertyChanged(nameof(DoesResetSpeed));
                    RaisePropertyChanged(nameof(DoesResetAcceleration));
                    RaisePropertyChanged(nameof(DoesResetDeceleration));
                    RaisePropertyChanged(nameof(DoesResetState));
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool DoesResetSpeed
        {
            get { return resetProperties.HasFlag(ResettableProperties.Speed); }
        }

        [AspectProperty(IsVisible = false)]
        public bool DoesResetAcceleration
        {
            get { return resetProperties.HasFlag(ResettableProperties.Acceleration); }
        }

        [AspectProperty(IsVisible = false)]
        public bool DoesResetDeceleration
        {
            get { return resetProperties.HasFlag(ResettableProperties.Deceleration); }
        }

        [AspectProperty(IsVisible = false)]
        public bool DoesResetState
        {
            get { return resetProperties.HasFlag(ResettableProperties.State); }
        }

        [AspectProperty, Category("Reset"), Description("Reset speed of the motor")]
        [DefaultValue(0.0)]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(SpeedUnits), IsVisiblePropertyLink = nameof(DoesResetSpeed))]
        public double ResetSpeed
        {
            get { return resetSpeed; }
            set { SetProperty(ref resetSpeed, value); }
        }

        [AspectProperty, Category("Reset"), Description("Reset acceleration of the motor")]
        [DefaultValue(0.0)]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(AccelerationUnits), IsVisiblePropertyLink = nameof(DoesResetAcceleration))]
        public double ResetAcceleration
        {
            get { return resetAcceleration; }
            set { SetProperty(ref resetAcceleration, value); }
        }

        [AspectProperty, Category("Reset"), Description("Reset deceleration of the motor")]
        [DefaultValue(0.0)]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(AccelerationUnits), IsVisiblePropertyLink = nameof(DoesResetDeceleration))]
        public double ResetDeceleration
        {
            get { return resetDeceleration; }
            set { SetProperty(ref resetDeceleration, value); }
        }

        [AspectProperty, Category("Reset"), Description("Reset state of the motor")]
        [DefaultValue(true)]
        [AspectEditor(IsVisiblePropertyLink = nameof(DoesResetState))]
        public bool ResetState
        {
            get { return resetState; }
            set { SetProperty(ref resetState, value); }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<double> SpeedBindableItem
        {
            get { return speedBindableItem; }
            private set
            {
                if (speedBindableItem != value)
                {
                    var speed = Speed;

                    if (speedBindableItem != null)
                    {
                        speedBindableItem.ValueChanged -= OnSpeedBindableItemChanged;
                        speedBindableItem.DetachFromVisual();
                        speedBindableItem = null;
                    }

                    speedBindableItem = value;
                    if (speedBindableItem != null)
                    {
                        speedBindableItem.ValueChanged += OnSpeedBindableItemChanged;
                        speedBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
                        speedBindableItem.Value = speed;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<double> AccelerationBindableItem
        {
            get { return accelerationBindableItem; }
            private set
            {
                if (accelerationBindableItem != value)
                {
                    var acceleration = Acceleration;

                    if (accelerationBindableItem != null)
                    {
                        accelerationBindableItem.ValueChanged -= OnAccelerationBindableItemChanged;
                        accelerationBindableItem.DetachFromVisual();
                        accelerationBindableItem = null;
                    }

                    accelerationBindableItem = value;
                    if (accelerationBindableItem != null)
                    {
                        accelerationBindableItem.ValueChanged += OnAccelerationBindableItemChanged;
                        accelerationBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
                        accelerationBindableItem.Value = acceleration;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<double> DecelerationBindableItem
        {
            get { return decelerationBindableItem; }
            private set
            {
                if (decelerationBindableItem != value)
                {
                    var deceleration = Deceleration;

                    if (decelerationBindableItem != null)
                    {
                        decelerationBindableItem.ValueChanged -= OnDecelerationBindableItemChanged;
                        decelerationBindableItem.DetachFromVisual();
                        decelerationBindableItem = null;
                    }

                    decelerationBindableItem = value;
                    if (decelerationBindableItem != null)
                    {
                        decelerationBindableItem.ValueChanged += OnDecelerationBindableItemChanged;
                        decelerationBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
                        decelerationBindableItem.Value = deceleration;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<bool> StateBindableItem
        {
            get { return stateBindableItem; }
            private set
            {
                if (stateBindableItem != value)
                {
                    var state = State;

                    if (stateBindableItem != null)
                    {
                        stateBindableItem.ValueChanged -= OnStateBindableItemChanged;
                        stateBindableItem.DetachFromVisual();
                        stateBindableItem = null;
                    }

                    stateBindableItem = value;
                    if (stateBindableItem != null)
                    {
                        stateBindableItem.ValueChanged += OnStateBindableItemChanged;
                        stateBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
                        stateBindableItem.Value = state;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<bool> AtExtendedBindableItem
        {
            get { return atExtendedBindableItem; }
            private set
            {
                if (atExtendedBindableItem != value)
                {
                    var atExtended = AtExtended;

                    if (atExtendedBindableItem != null)
                    {
                        atExtendedBindableItem.ValueChanged -= OnAtExtendedBindableItemChanged;
                        atExtendedBindableItem.DetachFromVisual();
                        atExtendedBindableItem = null;
                    }

                    atExtendedBindableItem = value;
                    if (atExtendedBindableItem != null)
                    {
                        atExtendedBindableItem.ValueChanged += OnAtExtendedBindableItemChanged;
                        atExtendedBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                        atExtendedBindableItem.Value = atExtended;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<bool> AtRetractedBindableItem
        {
            get { return atRetractedBindableItem; }
            private set
            {
                if (atRetractedBindableItem != value)
                {
                    var atRetracted = AtRetracted;

                    if (atRetractedBindableItem != null)
                    {
                        atRetractedBindableItem.ValueChanged -= OnAtRetractedBindableItemChanged;
                        atRetractedBindableItem.DetachFromVisual();
                        atRetractedBindableItem = null;
                    }

                    atRetractedBindableItem = value;
                    if (atRetractedBindableItem != null)
                    {
                        atRetractedBindableItem.ValueChanged += OnAtRetractedBindableItemChanged;
                        atRetractedBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                        atRetractedBindableItem.Value = atRetracted;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<bool> IsMovingBindableItem
        {
            get { return isMovingBindableItem; }
            private set
            {
                if (isMovingBindableItem != value)
                {
                    var isMoving = IsMoving;

                    if (isMovingBindableItem != null)
                    {
                        isMovingBindableItem.ValueChanged -= OnIsMovingBindableItemChanged;
                        isMovingBindableItem.DetachFromVisual();
                        isMovingBindableItem = null;
                    }

                    isMovingBindableItem = value;
                    if (isMovingBindableItem != null)
                    {
                        isMovingBindableItem.ValueChanged += OnIsMovingBindableItemChanged;
                        isMovingBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                        isMovingBindableItem.Value = isMoving;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public IEnumerable<BindableItem> BindableItems
        {
            get
            {
                if (ActivateBindableItem != null) { yield return ActivateBindableItem; }
                if (ExtendBindableItem != null) { yield return ExtendBindableItem; }
                if (RetractBindableItem != null) { yield return RetractBindableItem; }

                if (SpeedBindableItem != null) { yield return SpeedBindableItem; }
                if (AccelerationBindableItem != null) { yield return AccelerationBindableItem; }
                if (DecelerationBindableItem != null) { yield return DecelerationBindableItem; }
                if (StateBindableItem != null) { yield return StateBindableItem; }

                if (AtExtendedBindableItem != null) { yield return AtExtendedBindableItem; }
                if (AtRetractedBindableItem != null) { yield return AtRetractedBindableItem; }
                if (IsMovingBindableItem != null) { yield return IsMovingBindableItem; }
            }
        }

        public AdvancedActuationController()
        {
            motor = null;

            controlMode = ControlModes.ExtendRetract;
            swapPositions = false;

            activate = false;
            extend = false;
            retract = false;
            isActivateVisible = false;
            isExtendVisible = true;
            isRetractVisible = true;

            speed = 0.0;
            resetSpeed = 0.0;
            acceleration = 0.0;
            resetAcceleration = 0.0;
            deceleration = 0.0;
            resetDeceleration = 0.0;
            state = true;
            resetState = true;

            atExtended = false;
            atRetracted = true;
            IsMoving = false;

            resetProperties = ResettableProperties.None;

            activateBindableItem = null;
            extendBindableItem = null;
            retractBindableItem = null;

            speedBindableItem = null;
            accelerationBindableItem = null;
            decelerationBindableItem = null;
            stateBindableItem = null;

            atExtendedBindableItem = null;
            atRetractedBindableItem = null;
            IsMovingBindableItem = null;

            stateLock = null;
            directionLock = null;
            speedLock = null;
            accelerationLock = null;
            decelerationLock = null;

            prompt = null;
        }

        protected override void OnAssigned()
        {
            base.OnAssigned();

            CleanupBindingAPI();

            if (Visual != null)
            {
                CreateBindings();
            }
            else
            {
                RemoveBindings();
            }
        }

        protected override void OnRemoved()
        {
            if (motor != null)
            {
                motor.Controller = null; // Detach the controller from the motor.
            }

            ResetPrompt();

            if (prompt != null)
            {
                prompt.Subscribers -= OnMotionEvent;
                prompt = null;
            }

            base.OnRemoved();
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();

            UpdateAtExtendedRetracted();
            IsMoving = false;

            // delay any initial controller movement until the motor is initialized
            double seconds = document.Scene.EventTimeStep;
            var secondsAsFixed = Demo3D.EventQueue.Fixed.RoundUp(ref seconds);
            Visual.Document.EventQueue.ScheduleAction(secondsAsFixed, UpdatePrompt);
            Visual.Document.EventQueue.ScheduleAction(secondsAsFixed, UpdateAtExtendedRetracted);
        }

        protected override void OnReset()
        {
            base.OnReset();

            UpdateAtExtendedRetracted();
            IsMoving = false;
            ResetPrompt();
        }

        public bool Attach(MotorAspect motor)
        {
            // If we are already attached to a motor, then the attachment should fail.
            if (this.motor == null && motor != null)
            {
                this.motor = motor;
                if (this.motor != null)
                {
                    // If we can't lock all the properties we need then the attachment should fail.
                    if (TryLockProperties())
                    {
                        // Attachment succeeded!
                        Subscribe();
                        RaisePropertyChanged(nameof(PositionUnits)); // Update the units displayed in the GUI.
                        RaisePropertyChanged(nameof(SpeedUnits));
                        RaisePropertyChanged(nameof(AccelerationUnits));
                        RaisePropertyChanged(nameof(IsAngular));
                    }
                    else
                    {
                        this.motor = null;
                        RaisePropertyChanged(nameof(Motor));
                        return false;
                    }
                }

                RaisePropertyChanged(nameof(Motor));
                return true;
            }

            return false;
        }

        public void Detach()
        {
            if (motor != null)
            {
                UnlockProperties();
                Unsubscribe();
                motor = null;

                RaisePropertyChanged(nameof(Motor));
                RaisePropertyChanged(nameof(PositionUnits));
                RaisePropertyChanged(nameof(SpeedUnits));
                RaisePropertyChanged(nameof(AccelerationUnits));
                RaisePropertyChanged(nameof(IsAngular));
            }
        }

        public void Pause()
        {
            ResetPrompt(); // Existing event times are invalidated if the motor is paused.
        }

        public void Resume(double pauseDuration)
        {
            if (IsInitialized)
            {
                UpdatePrompt();
            }
        }

        private void CreateBindings()
        {
            Debug.Assert(Visual != null);

            ActivateBindableItem = new ReadFromServer<bool>(Visual, BindingName(nameof(Activate)));
            ExtendBindableItem = new ReadFromServer<bool>(Visual, BindingName(nameof(Extend)));
            RetractBindableItem = new ReadFromServer<bool>(Visual, BindingName(nameof(Retract)));
            SpeedBindableItem = new ReadFromServer<double>(Visual, BindingName(nameof(Speed)));
            AccelerationBindableItem = new ReadFromServer<double>(Visual, BindingName(nameof(Acceleration)));
            DecelerationBindableItem = new ReadFromServer<double>(Visual, BindingName(nameof(Deceleration)));
            StateBindableItem = new ReadFromServer<bool>(Visual, BindingName(nameof(State)));

            AtExtendedBindableItem = new WriteToServer<bool>(Visual, BindingName(nameof(AtExtended)));
            AtRetractedBindableItem = new WriteToServer<bool>(Visual, BindingName(nameof(AtRetracted)));
            IsMovingBindableItem = new WriteToServer<bool>(Visual, BindingName(nameof(IsMoving)));

            UpdateBindings();
        }

        private void RemoveBindings()
        {
            ActivateBindableItem = null;
            ExtendBindableItem = null;
            RetractBindableItem = null;
            SpeedBindableItem = null;
            AccelerationBindableItem = null;
            DecelerationBindableItem = null;
            StateBindableItem = null;

            AtExtendedBindableItem = null;
            AtRetractedBindableItem = null;
            IsMovingBindableItem = null;

            ReleaseBindingName(nameof(Activate));
            ReleaseBindingName(nameof(Extend));
            ReleaseBindingName(nameof(Retract));
            ReleaseBindingName(nameof(Speed));
            ReleaseBindingName(nameof(Acceleration));
            ReleaseBindingName(nameof(Deceleration));
            ReleaseBindingName(nameof(State));

            ReleaseBindingName(nameof(AtExtended));
            ReleaseBindingName(nameof(AtRetracted));
            ReleaseBindingName(nameof(IsMoving));

            UpdateBindingAPI();
        }

        private void UpdateBindings()
        {
            if (ActivateBindableItem != null) { ActivateBindableItem.IsBindingInterface = isActivateVisible ? TriStateYNM.Yes : TriStateYNM.No; }
            if (ExtendBindableItem != null) { ExtendBindableItem.IsBindingInterface = isExtendVisible ? TriStateYNM.Yes : TriStateYNM.No; }
            if (RetractBindableItem != null) { RetractBindableItem.IsBindingInterface = isRetractVisible ? TriStateYNM.Yes : TriStateYNM.No; }

            if (SpeedBindableItem != null) { SpeedBindableItem.IsBindingInterface = InputSpeed ? TriStateYNM.Yes : TriStateYNM.No; }
            if (AccelerationBindableItem != null) { AccelerationBindableItem.IsBindingInterface = InputAcceleration ? TriStateYNM.Yes : TriStateYNM.No; }
            if (DecelerationBindableItem != null) { DecelerationBindableItem.IsBindingInterface = InputDeceleration ? TriStateYNM.Yes : TriStateYNM.No; }
            if (StateBindableItem != null) { StateBindableItem.IsBindingInterface = InputState ? TriStateYNM.Yes : TriStateYNM.No; }

            if (AtExtendedBindableItem != null) { AtExtendedBindableItem.IsBindingInterface = Outputs.HasFlag(Output.AtExtended) ? TriStateYNM.Yes : TriStateYNM.No; }
            if (AtRetractedBindableItem != null) { AtRetractedBindableItem.IsBindingInterface = Outputs.HasFlag(Output.AtRetracted) ? TriStateYNM.Yes : TriStateYNM.No; }
            if (IsMovingBindableItem != null) { IsMovingBindableItem.IsBindingInterface = Outputs.HasFlag(Output.IsMoving) ? TriStateYNM.Yes : TriStateYNM.No; }

            UpdateBindingAPI();
        }

        private void UnlockProperties()
        {
            stateLock?.Dispose();
            stateLock = null;

            directionLock?.Dispose();
            directionLock = null;

            speedLock?.Dispose();
            speedLock = null;

            accelerationLock?.Dispose();
            accelerationLock = null;

            decelerationLock?.Dispose();
            decelerationLock = null;
        }

        private bool TryLockProperties()
        {
            if (stateLock == null) { stateLock = this.motor.TryLockTargetState(); }
            if (directionLock == null) { directionLock = this.motor.TryLockTargetDirection(); }
            if (stateLock == null || directionLock == null) { UnlockProperties(); return false; }

            if (InputSpeed)
            {
                if (speedLock == null)
                {
                    speedLock = this.motor.TryLockTargetSpeed();
                    if (speedLock != null)
                    {
                        speed = speedLock.Get();
                    }
                    else
                    {
                        UnlockProperties(); return false;
                    }
                }
            }
            else
            {
                speedLock?.Dispose();
                speedLock = null;
            }

            if (InputAcceleration)
            {
                if (accelerationLock == null)
                {
                    accelerationLock = this.motor.TryLockMaxAcceleration();
                    if (accelerationLock != null)
                    {
                        acceleration = accelerationLock.Get();
                    }
                    else
                    {
                        UnlockProperties(); return false;
                    }
                }
            }
            else
            {
                accelerationLock?.Dispose();
                accelerationLock = null;
            }

            if (InputDeceleration)
            {
                if (decelerationLock == null)
                {
                    decelerationLock = this.motor.TryLockMaxDeceleration();
                    if (decelerationLock != null)
                    {
                        deceleration = decelerationLock.Get();
                    }
                    else
                    {
                        UnlockProperties(); return false;
                    }
                }
            }
            else
            {
                decelerationLock?.Dispose();
                decelerationLock = null;
            }

            return true;
        }

        private void Subscribe()
        {
            motor.OnTargetSpeedChanged += OnMotorTargetSpeedChanged;
            motor.OnMaxAccelerationChanged += OnMotorMaxAccelerationChanged;
            motor.OnMaxDecelerationChanged += OnMotorMaxDecelerationChanged;
            motor.OnUnitsChanged += OnUnitsChanged;
            motor.OnAfterReset += OnMotorReset;
            motor.OnPositionLimitChanged += OnPositionLimitChanged;
        }

        private void Unsubscribe()
        {
            motor.OnAfterReset -= OnMotorReset;
            motor.OnUnitsChanged -= OnUnitsChanged;
            motor.OnMaxDecelerationChanged -= OnMotorMaxDecelerationChanged;
            motor.OnMaxAccelerationChanged -= OnMotorMaxAccelerationChanged;
            motor.OnTargetSpeedChanged -= OnMotorTargetSpeedChanged;
            motor.OnPositionLimitChanged -= OnPositionLimitChanged;
        }

        private void ResetPrompt()
        {
            prompt?.Reset();
        }

        private void UpdatePrompt()
        {
            if (Visual == null || motor == null) { return; }

            if (prompt == null)
            {
                prompt = new MotionPrompt(Visual);
                prompt.Subscribers += OnMotionEvent;
            }
            else
            {
                prompt.Reset();
            }

            // current motor configuration
            var motorDirection = (motor.Direction == MotorDirection.Forwards) ? MotionDirection.Forwards : MotionDirection.Reverse;
            var currentState = motor.Current();
            var currentDirection = (currentState.Velocity == 0.0) ? motorDirection : ((currentState.Velocity >= 0.0) ? MotionDirection.Forwards : MotionDirection.Reverse);
            var currentSpeed = Math.Abs(currentState.Velocity);
            var currentPosition = motor.Current().Position;

            // calculate target speed and target position as signed displacement
            var targetSpeed = state ? motor.TargetSpeed : 0.0;
            var targetPosition = 0.0;
            if (ControlMode == ControlModes.Activate)
            {
                if (Activate)
                {
                    targetPosition = SwapPositions ? motor.LowerPositionLimit : motor.UpperPositionLimit;
                }
                else
                {
                    targetPosition = SwapPositions ? motor.UpperPositionLimit : motor.LowerPositionLimit;
                }
            }
            else if (ControlMode == ControlModes.ExtendRetract)
            {
                if (Extend && !Retract)
                {
                    targetPosition = SwapPositions ? motor.LowerPositionLimit : motor.UpperPositionLimit;
                }
                else if (!Extend && Retract)
                {
                    targetPosition = SwapPositions ? motor.UpperPositionLimit : motor.LowerPositionLimit;
                }
                else
                {
                    targetSpeed = 0.0;
                    targetPosition = (motor.Direction == MotorDirection.Forwards) ? motor.UpperPositionLimit : motor.LowerPositionLimit;
                }
            }
            double signedDisplacement = targetPosition - currentPosition;
            if (targetType == TargetType.Continuous && IsAngular)
            {
                signedDisplacement = KJEUtil.NormalizePlusMinusOneEighty(targetPosition - currentPosition);
            }

            var motionProfile = new MotionProfile(currentDirection, currentSpeed, targetSpeed, motor.MaxAcceleration, motor.MaxDeceleration);

            if (!prompt.Update(motionProfile, signedDisplacement))
            {
                // Just switch the motor off, since it could be that the motor is currently switched
                // on but the speed is zero (D-8806).
                SetMotorState(MotorState.Off);
            }
        }

        private void SetMotorState(MotorState newState)
        {
            stateLock.Set(newState);
        }

        private void SetMotorDirection(MotorDirection newDirection)
        {
            directionLock.Set(newDirection);
        }

        private void SetMotorSpeed(double newSpeed)
        {
            if (speedLock != null)
            {
                speedLock.Set(newSpeed);
            }
        }

        private void SetMotorAcceleration(double newAcceleration)
        {
            if (accelerationLock != null)
            {
                accelerationLock.Set(newAcceleration);
            }
        }

        private void SetMotorDeceleration(double newDeceleration)
        {
            if (decelerationLock != null)
            {
                decelerationLock.Set(newDeceleration);
            }
        }

        private void UpdateAtExtendedRetracted()
        {
            if (motor != null)
            {
                // ignore nuisance exceptions (may occur OnReset)
                try
                {
                    var position = motor.Current().Position;
                    if (Util.EQ(position, motor.UpperPositionLimit, 0.001))
                    {
                        AtExtended = SwapPositions ? false : true;
                        AtRetracted = SwapPositions ? true : false;
                        return;
                    }
                    if (Util.EQ(position, motor.LowerPositionLimit, 0.001))
                    {
                        AtExtended = SwapPositions ? true : false;
                        AtRetracted = SwapPositions ? false : true;
                        return;
                    }
                }
                catch (Exception) { }
            }

            ResetAtExtendedRetracted();
        }

        private void ResetAtExtendedRetracted()
        {
            AtExtended = false;
            AtRetracted = false;
        }

        private void OnActivateBindableItemChanged(BindableItem item)
        {
            Activate = item.ValueAs<bool>();
        }

        private void OnExtendBindableItemChanged(BindableItem item)
        {
            Extend = item.ValueAs<bool>();
        }

        private void OnRetractBindableItemChanged(BindableItem item)
        {
            Retract = item.ValueAs<bool>();
        }

        private void OnSpeedBindableItemChanged(BindableItem item)
        {
            Speed = item.ValueAs<double>();
        }

        private void OnAccelerationBindableItemChanged(BindableItem item)
        {
            Acceleration = item.ValueAs<double>();
        }

        private void OnDecelerationBindableItemChanged(BindableItem item)
        {
            Deceleration = item.ValueAs<double>();
        }

        private void OnStateBindableItemChanged(BindableItem item)
        {
            State = item.ValueAs<bool>();
        }

        private void OnAtExtendedBindableItemChanged(BindableItem item)
        {
            AtExtended = item.ValueAs<bool>();
        }

        private void OnAtRetractedBindableItemChanged(BindableItem item)
        {
            AtRetracted = item.ValueAs<bool>();
        }

        private void OnIsMovingBindableItemChanged(BindableItem item)
        {
            IsMoving = item.ValueAs<bool>();
        }

        private void OnPositionLimitChanged(MotorAspect motor, double oldMinPosition, double oldMaxPosition, double newMinPosition, double newMaxPosition)
        {
            if (IsInitialized)
            {
                UpdatePrompt();
            }
        }

        private void OnMotorMaxDecelerationChanged(MotorAspect motor, double oldMaxDeceleration, double newMaxDeceleration)
        {
            if (IsInitialized)
            {
                UpdatePrompt();
            }
        }

        private void OnMotorMaxAccelerationChanged(MotorAspect motor, double oldMaxAcceleration, double newMaxAcceleration)
        {
            if (IsInitialized)
            {
                UpdatePrompt();
            }
        }

        private void OnUnitsChanged(MotorAspect motor, MotorAspect.Units oldUnits, MotorAspect.Units newUnits)
        {
            RaisePropertyChanged(nameof(PositionUnits));
            RaisePropertyChanged(nameof(SpeedUnits));
            RaisePropertyChanged(nameof(AccelerationUnits));
            RaisePropertyChanged(nameof(IsAngular));
        }

        private void OnMotorTargetSpeedChanged(MotorAspect motor, double oldTargetSpeed, double newTargetSpeed)
        {
            if (IsInitialized)
            {
                UpdatePrompt();
            }
        }

        private void OnMotorReset(MotorAspect motor)
        {
            if (resetProperties.HasFlag(ResettableProperties.Speed))
            {
                Speed = resetSpeed;
            }

            if (resetProperties.HasFlag(ResettableProperties.Acceleration))
            {
                Acceleration = resetAcceleration;
            }

            if (resetProperties.HasFlag(ResettableProperties.Deceleration))
            {
                Deceleration = resetDeceleration;
            }

            if (resetProperties.HasFlag(ResettableProperties.State))
            {
                State = resetState;
            }

            UpdateAtExtendedRetracted();
        }

        private void OnMotionEvent(MotionEventType eventType)
        {
            switch (eventType)
            {
                case MotionEventType.Steady:
                    SetMotorState(MotorState.On);
                    IsMoving = true;
                    break;
                case MotionEventType.Decelerate:
                    SetMotorState(MotorState.Off);
                    IsMoving = true;
                    break;
                case MotionEventType.Stop:
                    SetMotorState(MotorState.Off);
                    UpdateAtExtendedRetracted();
                    IsMoving = false;
                    break;
                case MotionEventType.Accelerate:
                    SetMotorState(MotorState.On);
                    IsMoving = true;
                    break;
                case MotionEventType.Start:
                    SetMotorState(MotorState.On);
                    ResetAtExtendedRetracted();
                    IsMoving = true;
                    break;
                case MotionEventType.ChangeDirectionForwards:
                    SetMotorDirection(MotorDirection.Forwards);
                    IsMoving = true;
                    break;
                case MotionEventType.ChangeDirectionReverse:
                    SetMotorDirection(MotorDirection.Reverse);
                    IsMoving = true;
                    break;
            }
        }
    }
}
