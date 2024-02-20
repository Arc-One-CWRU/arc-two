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
    [HelpUrl("advanced_position_controller")]
    public sealed class AdvancedPositionController : ExportableVisualAspect, IController, IBindableItemOwner
    {
        public enum TargetType
        {
            Discontinuous,
            Continuous
        }

        [Flags]
        public enum ResettableProperties
        {
            None = 0,
            TargetPosition = 1 << 0,
            Speed = 1 << 1,
            Acceleration = 1 << 2,
            Deceleration = 1 << 3,
            State = 1 << 4
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
            AtTargetPosition = 1 << 0,
            IsAccelerating = 1 << 1,
            IsDecelerating = 1 << 2,
            AtSpeed = 1 << 3,
            IsForwards = 1 << 4,
            IsReverse = 1 << 5,
            IsMoving = 1 << 6
        }

        private MotorAspect motor;
        private Input inputs;
        private Output outputs;

        private TargetType targetType;

        private double targetPosition;
        private double resetTargetPosition;
        private double speed;
        private double resetSpeed;
        private double acceleration;
        private double resetAcceleration;
        private double deceleration;
        private double resetDeceleration;
        private bool state;
        private bool resetState;

        private bool atTargetPosition;
        private bool isAccelerating;
        private bool isDecelerating;
        private bool atSpeed;
        private bool isForwards;
        private bool isReverse;
        private bool isMoving;

        private ResettableProperties resetProperties;

        private BindableItem<double> targetPositionBindableItem;
        private BindableItem<double> speedBindableItem;
        private BindableItem<double> accelerationBindableItem;
        private BindableItem<double> decelerationBindableItem;
        private BindableItem<bool> stateBindableItem;

        private BindableItem<bool> atTargetPositionBindableItem;
        private BindableItem<bool> isAcceleratingBindableItem;
        private BindableItem<bool> isDeceleratingBindableItem;
        private BindableItem<bool> atSpeedBindableItem;
        private BindableItem<bool> isForwardsBindableItem;
        private BindableItem<bool> isReverseBindableItem;
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

        [AspectProperty, Category("Inputs"), Description("Target position of the motor")]
        [DefaultValue(0.0)]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(PositionUnits))]
        public double TargetPosition
        {
            get { return targetPosition; }
            set
            {
                if (TargetPositionBindableItem != null)
                {
                    if (TargetPositionBindableItem.ValueAs<double>() != value)
                    {
                        TargetPositionBindableItem.Value = value;
                    }
                }

                if (SetProperty(ref targetPosition, value))
                {
                    if (IsInitialized)
                    {
                        UpdatePrompt();
                    }

                    UpdateAtTargetPosition();
                }
            }
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

                    RaisePropertyChanged(nameof(OutputAtTargetPosition));
                    RaisePropertyChanged(nameof(AtTargetPosition));
                    RaisePropertyChanged(nameof(OutputIsAccelerating));
                    RaisePropertyChanged(nameof(IsAccelerating));
                    RaisePropertyChanged(nameof(OutputIsDecelerating));
                    RaisePropertyChanged(nameof(IsDecelerating));
                    RaisePropertyChanged(nameof(OutputAtSpeed));
                    RaisePropertyChanged(nameof(AtSpeed));
                    RaisePropertyChanged(nameof(OutputIsForwards));
                    RaisePropertyChanged(nameof(IsForwards));
                    RaisePropertyChanged(nameof(OutputIsReverse));
                    RaisePropertyChanged(nameof(IsReverse));
                    RaisePropertyChanged(nameof(OutputIsMoving));
                    RaisePropertyChanged(nameof(IsMoving));
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool OutputAtTargetPosition
        {
            get { return outputs.HasFlag(Output.AtTargetPosition); }
        }

        [AspectProperty, Category("Outputs"), Description("Motor at target position")]
        [AspectEditor(IsVisiblePropertyLink = nameof(OutputAtTargetPosition), IsEnabledPropertyLink = nameof(OutputAtTargetPosition))]
        public bool AtTargetPosition
        {
            get { return atTargetPosition; }
            private set
            {
                if (AtTargetPositionBindableItem != null)
                {
                    if (AtTargetPositionBindableItem.ValueAs<bool>() != value)
                    {
                        AtTargetPositionBindableItem.Value = value;
                    }
                }

                SetProperty(ref atTargetPosition, value);
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool OutputIsAccelerating
        {
            get { return outputs.HasFlag(Output.IsAccelerating); }
        }

        [AspectProperty, Category("Outputs"), Description("Whether the motor is accelerating")]
        [AspectEditor(IsVisiblePropertyLink = nameof(OutputIsAccelerating), IsEnabledPropertyLink = nameof(OutputIsAccelerating))]
        public bool IsAccelerating
        {
            get { return isAccelerating; }
            private set
            {
                if (IsAcceleratingBindableItem != null)
                {
                    if (IsAcceleratingBindableItem.ValueAs<bool>() != value)
                    {
                        IsAcceleratingBindableItem.Value = value;
                    }
                }

                SetProperty(ref isAccelerating, value);
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool OutputIsDecelerating
        {
            get { return outputs.HasFlag(Output.IsDecelerating); }
        }

        [AspectProperty, Category("Outputs"), Description("Whether the motor is decelerating")]
        [AspectEditor(IsVisiblePropertyLink = nameof(OutputIsDecelerating), IsEnabledPropertyLink = nameof(OutputIsDecelerating))]
        public bool IsDecelerating
        {
            get { return isDecelerating; }
            private set
            {
                if (IsDeceleratingBindableItem != null)
                {
                    if (IsDeceleratingBindableItem.ValueAs<bool>() != value)
                    {
                        IsDeceleratingBindableItem.Value = value;
                    }
                }

                SetProperty(ref isDecelerating, value);
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool OutputAtSpeed
        {
            get { return outputs.HasFlag(Output.AtSpeed); }
        }

        [AspectProperty, Category("Outputs"), Description("Whether the motor is at the target speed")]
        [AspectEditor(IsVisiblePropertyLink = nameof(OutputAtSpeed), IsEnabledPropertyLink = nameof(OutputAtSpeed))]
        public bool AtSpeed
        {
            get { return atSpeed; }
            private set
            {
                if (AtSpeedBindableItem != null)
                {
                    if (AtSpeedBindableItem.ValueAs<bool>() != value)
                    {
                        AtSpeedBindableItem.Value = value;
                    }
                }

                SetProperty(ref atSpeed, value);
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool OutputIsForwards
        {
            get { return outputs.HasFlag(Output.IsForwards); }
        }

        [AspectProperty, Category("Outputs"), Description("Whether the motor is running forwards")]
        [AspectEditor(IsVisiblePropertyLink = nameof(OutputIsForwards), IsEnabledPropertyLink = nameof(OutputIsForwards))]
        public bool IsForwards
        {
            get { return isForwards; }
            private set
            {
                if (IsForwardsBindableItem != null)
                {
                    if (IsForwardsBindableItem.ValueAs<bool>() != value)
                    {
                        IsForwardsBindableItem.Value = value;
                    }
                }

                SetProperty(ref isForwards, value);
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool OutputIsReverse
        {
            get { return outputs.HasFlag(Output.IsReverse); }
        }

        [AspectProperty, Category("Outputs"), Description("Whether the motor is running in reverse")]
        [AspectEditor(IsVisiblePropertyLink = nameof(OutputIsReverse), IsEnabledPropertyLink = nameof(OutputIsReverse))]
        public bool IsReverse
        {
            get { return isReverse; }
            private set
            {
                if (IsReverseBindableItem != null)
                {
                    if (IsReverseBindableItem.ValueAs<bool>() != value)
                    {
                        IsReverseBindableItem.Value = value;
                    }
                }

                SetProperty(ref isReverse, value);
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
                    RaisePropertyChanged(nameof(DoesResetTargetPosition));
                    RaisePropertyChanged(nameof(DoesResetSpeed));
                    RaisePropertyChanged(nameof(DoesResetAcceleration));
                    RaisePropertyChanged(nameof(DoesResetDeceleration));
                    RaisePropertyChanged(nameof(DoesResetState));
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool DoesResetTargetPosition
        {
            get { return resetProperties.HasFlag(ResettableProperties.TargetPosition); }
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

        [AspectProperty, Category("Reset"), Description("Reset target position of the motor")]
        [DefaultValue(0.0)]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(PositionUnits), IsVisiblePropertyLink = nameof(DoesResetTargetPosition))]
        public double ResetTargetPosition
        {
            get { return resetTargetPosition; }
            set { SetProperty(ref resetTargetPosition, value); }
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
        public BindableItem<double> TargetPositionBindableItem
        {
            get { return targetPositionBindableItem; }
            private set
            {
                if (targetPositionBindableItem != value)
                {
                    var targetPosition = TargetPosition;

                    if (targetPositionBindableItem != null)
                    {
                        targetPositionBindableItem.ValueChanged -= OnTargetPositionBindableItemChanged;
                        targetPositionBindableItem.DetachFromVisual();
                        targetPositionBindableItem = null;
                    }

                    targetPositionBindableItem = value;
                    if (targetPositionBindableItem != null)
                    {
                        targetPositionBindableItem.ValueChanged += OnTargetPositionBindableItemChanged;
                        targetPositionBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
                        targetPositionBindableItem.Value = targetPosition;
                    }
                }
            }
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
        public BindableItem<bool> AtTargetPositionBindableItem
        {
            get { return atTargetPositionBindableItem; }
            private set
            {
                if (atTargetPositionBindableItem != value)
                {
                    var atTargetPosition = AtTargetPosition;

                    if (atTargetPositionBindableItem != null)
                    {
                        atTargetPositionBindableItem.ValueChanged -= OnAtTargetPositionBindableItemChanged;
                        atTargetPositionBindableItem.DetachFromVisual();
                        atTargetPositionBindableItem = null;
                    }

                    atTargetPositionBindableItem = value;
                    if (atTargetPositionBindableItem != null)
                    {
                        atTargetPositionBindableItem.ValueChanged += OnAtTargetPositionBindableItemChanged;
                        atTargetPositionBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                        atTargetPositionBindableItem.Value = atTargetPosition;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<bool> IsAcceleratingBindableItem
        {
            get { return isAcceleratingBindableItem; }
            private set
            {
                if (isAcceleratingBindableItem != value)
                {
                    var isAccelerating = IsAccelerating;

                    if (isAcceleratingBindableItem != null)
                    {
                        isAcceleratingBindableItem.ValueChanged -= OnIsAcceleratingBindableItemChanged;
                        isAcceleratingBindableItem.DetachFromVisual();
                        isAcceleratingBindableItem = null;
                    }

                    isAcceleratingBindableItem = value;
                    if (isAcceleratingBindableItem != null)
                    {
                        isAcceleratingBindableItem.ValueChanged += OnIsAcceleratingBindableItemChanged;
                        isAcceleratingBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                        isAcceleratingBindableItem.Value = isAccelerating;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<bool> IsDeceleratingBindableItem
        {
            get { return isDeceleratingBindableItem; }
            private set
            {
                if (isDeceleratingBindableItem != value)
                {
                    var isDecelerating = IsDecelerating;

                    if (isDeceleratingBindableItem != null)
                    {
                        isDeceleratingBindableItem.ValueChanged -= OnIsDeceleratingBindableItemChanged;
                        isDeceleratingBindableItem.DetachFromVisual();
                        isDeceleratingBindableItem = null;
                    }

                    isDeceleratingBindableItem = value;
                    if (isDeceleratingBindableItem != null)
                    {
                        isDeceleratingBindableItem.ValueChanged += OnIsDeceleratingBindableItemChanged;
                        isDeceleratingBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                        isDeceleratingBindableItem.Value = isDecelerating;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<bool> AtSpeedBindableItem
        {
            get { return atSpeedBindableItem; }
            private set
            {
                if (atSpeedBindableItem != value)
                {
                    var atSpeed = AtSpeed;

                    if (atSpeedBindableItem != null)
                    {
                        atSpeedBindableItem.ValueChanged -= OnAtSpeedBindableItemChanged;
                        atSpeedBindableItem.DetachFromVisual();
                        atSpeedBindableItem = null;
                    }

                    atSpeedBindableItem = value;
                    if (atSpeedBindableItem != null)
                    {
                        atSpeedBindableItem.ValueChanged += OnAtSpeedBindableItemChanged;
                        atSpeedBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                        atSpeedBindableItem.Value = atSpeed;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<bool> IsForwardsBindableItem
        {
            get { return isForwardsBindableItem; }
            private set
            {
                if (isForwardsBindableItem != value)
                {
                    var isForwards = IsForwards;

                    if (isForwardsBindableItem != null)
                    {
                        isForwardsBindableItem.ValueChanged -= OnIsForwardsBindableItemChanged;
                        isForwardsBindableItem.DetachFromVisual();
                        isForwardsBindableItem = null;
                    }

                    isForwardsBindableItem = value;
                    if (isForwardsBindableItem != null)
                    {
                        isForwardsBindableItem.ValueChanged += OnIsForwardsBindableItemChanged;
                        isForwardsBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                        isForwardsBindableItem.Value = isForwards;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<bool> IsReverseBindableItem
        {
            get { return isReverseBindableItem; }
            private set
            {
                if (isReverseBindableItem != value)
                {
                    var isReverse = IsReverse;

                    if (isReverseBindableItem != null)
                    {
                        isReverseBindableItem.ValueChanged -= OnIsReverseBindableItemChanged;
                        isReverseBindableItem.DetachFromVisual();
                        isReverseBindableItem = null;
                    }

                    isReverseBindableItem = value;
                    if (isReverseBindableItem != null)
                    {
                        isReverseBindableItem.ValueChanged += OnIsReverseBindableItemChanged;
                        isReverseBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                        isReverseBindableItem.Value = isReverse;
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
                if (TargetPositionBindableItem != null) { yield return TargetPositionBindableItem; }
                if (SpeedBindableItem != null) { yield return SpeedBindableItem; }
                if (AccelerationBindableItem != null) { yield return AccelerationBindableItem; }
                if (DecelerationBindableItem != null) { yield return DecelerationBindableItem; }
                if (StateBindableItem != null) { yield return StateBindableItem; }

                if (AtTargetPositionBindableItem != null) { yield return AtTargetPositionBindableItem; }
                if (IsAcceleratingBindableItem != null) { yield return IsAcceleratingBindableItem; }
                if (IsDeceleratingBindableItem != null) { yield return IsDeceleratingBindableItem; }
                if (AtSpeedBindableItem != null) { yield return AtSpeedBindableItem; }
                if (IsForwardsBindableItem != null) { yield return IsForwardsBindableItem; }
                if (IsReverseBindableItem != null) { yield return IsReverseBindableItem; }
                if (IsMovingBindableItem != null) { yield return IsMovingBindableItem; }
            }
        }

        public AdvancedPositionController()
        {
            motor = null;

            targetPosition = 0.0;
            resetTargetPosition = 0.0;
            speed = 0.0;
            resetSpeed = 0.0;
            acceleration = 0.0;
            resetAcceleration = 0.0;
            deceleration = 0.0;
            resetDeceleration = 0.0;
            state = true;
            resetState = true;

            atTargetPosition = false;
            isAccelerating = false;
            isDecelerating = false;
            atSpeed = false;
            isForwards = false;
            IsReverse = false;
            IsMoving = false;

            resetProperties = ResettableProperties.None;

            targetPositionBindableItem = null;
            speedBindableItem = null;
            accelerationBindableItem = null;
            decelerationBindableItem = null;
            stateBindableItem = null;

            atTargetPositionBindableItem = null;
            isAcceleratingBindableItem = null;
            isDeceleratingBindableItem = null;
            atSpeedBindableItem = null;
            IsForwardsBindableItem = null;
            IsReverseBindableItem = null;
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

            UpdateAtTargetPosition();
            IsAccelerating = false;
            IsDecelerating = false;
            AtSpeed = false;
            IsMoving = false;
            UpdateIsForwardsIsReverse();

            // delay any initial controller movement until the motor is initialized
            double seconds = document.Scene.EventTimeStep;
            var secondsAsFixed = Demo3D.EventQueue.Fixed.RoundUp(ref seconds);
            Visual.Document.EventQueue.ScheduleAction(secondsAsFixed, UpdatePrompt);
            Visual.Document.EventQueue.ScheduleAction(secondsAsFixed, UpdateAtTargetPosition);
            Visual.Document.EventQueue.ScheduleAction(secondsAsFixed, UpdateIsForwardsIsReverse);
        }

        protected override void OnReset()
        {
            base.OnReset();

            UpdateAtTargetPosition();
            IsAccelerating = false;
            IsDecelerating = false;
            AtSpeed = false;
            IsMoving = false;
            UpdateIsForwardsIsReverse();

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

            TargetPositionBindableItem = new ReadFromServer<double>(Visual, BindingName(nameof(TargetPosition)));
            SpeedBindableItem = new ReadFromServer<double>(Visual, BindingName(nameof(Speed)));
            AccelerationBindableItem = new ReadFromServer<double>(Visual, BindingName(nameof(Acceleration)));
            DecelerationBindableItem = new ReadFromServer<double>(Visual, BindingName(nameof(Deceleration)));
            StateBindableItem = new ReadFromServer<bool>(Visual, BindingName(nameof(State)));

            AtTargetPositionBindableItem = new WriteToServer<bool>(Visual, BindingName(nameof(AtTargetPosition)));
            IsAcceleratingBindableItem = new WriteToServer<bool>(Visual, BindingName(nameof(IsAccelerating)));
            IsDeceleratingBindableItem = new WriteToServer<bool>(Visual, BindingName(nameof(IsDecelerating)));
            AtSpeedBindableItem = new WriteToServer<bool>(Visual, BindingName(nameof(AtSpeed)));
            IsForwardsBindableItem = new WriteToServer<bool>(Visual, BindingName(nameof(IsForwards)));
            IsReverseBindableItem = new WriteToServer<bool>(Visual, BindingName(nameof(IsReverse)));
            IsMovingBindableItem = new WriteToServer<bool>(Visual, BindingName(nameof(IsMoving)));

            UpdateBindings();
        }

        private void RemoveBindings()
        {
            TargetPositionBindableItem = null;
            SpeedBindableItem = null;
            AccelerationBindableItem = null;
            DecelerationBindableItem = null;
            StateBindableItem = null;

            AtTargetPositionBindableItem = null;
            IsAcceleratingBindableItem = null;
            IsDeceleratingBindableItem = null;
            AtSpeedBindableItem = null;
            IsForwardsBindableItem = null;
            IsReverseBindableItem = null;
            IsMovingBindableItem = null;

            ReleaseBindingName(nameof(TargetPosition));
            ReleaseBindingName(nameof(Speed));
            ReleaseBindingName(nameof(Acceleration));
            ReleaseBindingName(nameof(Deceleration));
            ReleaseBindingName(nameof(State));

            ReleaseBindingName(nameof(AtTargetPosition));
            ReleaseBindingName(nameof(IsAccelerating));
            ReleaseBindingName(nameof(IsDecelerating));
            ReleaseBindingName(nameof(AtSpeed));
            ReleaseBindingName(nameof(IsForwards));
            ReleaseBindingName(nameof(IsReverse));
            ReleaseBindingName(nameof(IsMoving));

            UpdateBindingAPI();
        }

        private void UpdateBindings()
        {
            if (TargetPositionBindableItem != null) { TargetPositionBindableItem.IsBindingInterface = TriStateYNM.Yes; }
            if (SpeedBindableItem != null) { SpeedBindableItem.IsBindingInterface = InputSpeed ? TriStateYNM.Yes : TriStateYNM.No; }
            if (AccelerationBindableItem != null) { AccelerationBindableItem.IsBindingInterface = InputAcceleration ? TriStateYNM.Yes : TriStateYNM.No; }
            if (DecelerationBindableItem != null) { DecelerationBindableItem.IsBindingInterface = InputDeceleration ? TriStateYNM.Yes : TriStateYNM.No; }
            if (StateBindableItem != null) { StateBindableItem.IsBindingInterface = InputState ? TriStateYNM.Yes : TriStateYNM.No; }

            if (AtTargetPositionBindableItem != null) { AtTargetPositionBindableItem.IsBindingInterface = Outputs.HasFlag(Output.AtTargetPosition) ? TriStateYNM.Yes : TriStateYNM.No; }
            if (IsAcceleratingBindableItem != null) { IsAcceleratingBindableItem.IsBindingInterface = Outputs.HasFlag(Output.IsAccelerating) ? TriStateYNM.Yes : TriStateYNM.No; }
            if (IsDeceleratingBindableItem != null) { IsDeceleratingBindableItem.IsBindingInterface = Outputs.HasFlag(Output.IsDecelerating) ? TriStateYNM.Yes : TriStateYNM.No; }
            if (AtSpeedBindableItem != null) { AtSpeedBindableItem.IsBindingInterface = Outputs.HasFlag(Output.AtSpeed) ? TriStateYNM.Yes : TriStateYNM.No; }
            if (IsForwardsBindableItem != null) { IsForwardsBindableItem.IsBindingInterface = Outputs.HasFlag(Output.IsForwards) ? TriStateYNM.Yes : TriStateYNM.No; }
            if (IsReverseBindableItem != null) { IsReverseBindableItem.IsBindingInterface = Outputs.HasFlag(Output.IsReverse) ? TriStateYNM.Yes : TriStateYNM.No; }
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
        }

        private void Unsubscribe()
        {
            motor.OnAfterReset -= OnMotorReset;
            motor.OnUnitsChanged -= OnUnitsChanged;
            motor.OnMaxDecelerationChanged -= OnMotorMaxDecelerationChanged;
            motor.OnMaxAccelerationChanged -= OnMotorMaxAccelerationChanged;
            motor.OnTargetSpeedChanged -= OnMotorTargetSpeedChanged;
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

            var currentState = motor.Current();
            var currentPosition = motor.Current().Position;

            double signedDisplacement = targetPosition - currentPosition;
            if (targetType == TargetType.Continuous && IsAngular)
            {
                signedDisplacement = KJEUtil.NormalizePlusMinusOneEighty(targetPosition - currentPosition);
            }

            var targetDirection = (signedDisplacement >= 0.0) ? MotorDirection.Forwards : MotorDirection.Reverse;
            var targetSpeed = (state) ? motor.TargetSpeed : 0.0;
            var motorDirection = (motor.Direction == MotorDirection.Forwards) ? MotionDirection.Forwards : MotionDirection.Reverse;
            var currentDirection = (currentState.Velocity == 0.0) ? motorDirection : ((currentState.Velocity >= 0.0) ? MotionDirection.Forwards : MotionDirection.Reverse);
            var currentSpeed = Math.Abs(currentState.Velocity);

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

        private void UpdateAtTargetPosition()
        {
            if (motor != null)
            {
                // ignore nuisance exceptions (may occur OnReset)
                try
                {
                    var position = motor.Current().Position;
                    if (targetType == TargetType.Continuous && IsAngular)
                    {
                        if (Util.EQ(KJEUtil.NormalizeZeroThreeSixty(position), KJEUtil.NormalizeZeroThreeSixty(targetPosition), 0.001))
                        {
                            AtTargetPosition = true;
                            return;
                        }
                    }
                    else
                    {
                        if (Util.EQ(position, targetPosition, 0.001))
                        {
                            AtTargetPosition = true;
                            return;
                        }
                    }
                }
                catch (Exception) { }
            }

            AtTargetPosition = false;
        }

        private void UpdateIsForwardsIsReverse()
        {
            if (motor != null)
            {
                IsForwards = motor.Direction == MotorDirection.Forwards;
                IsReverse = motor.Direction == MotorDirection.Reverse;
            }
        }

        private void OnTargetPositionBindableItemChanged(BindableItem item)
        {
            TargetPosition = item.ValueAs<double>();
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

        private void OnAtTargetPositionBindableItemChanged(BindableItem item)
        {
            AtTargetPosition = item.ValueAs<bool>();
        }

        private void OnIsAcceleratingBindableItemChanged(BindableItem item)
        {
            IsAccelerating = item.ValueAs<bool>();
        }

        private void OnIsDeceleratingBindableItemChanged(BindableItem item)
        {
            IsDecelerating = item.ValueAs<bool>();
        }

        private void OnAtSpeedBindableItemChanged(BindableItem item)
        {
            AtSpeed = item.ValueAs<bool>();
        }

        private void OnIsForwardsBindableItemChanged(BindableItem item)
        {
            IsForwards = item.ValueAs<bool>();
        }

        private void OnIsReverseBindableItemChanged(BindableItem item)
        {
            IsReverse = item.ValueAs<bool>();
        }

        private void OnIsMovingBindableItemChanged(BindableItem item)
        {
            IsMoving = item.ValueAs<bool>();
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
            if (resetProperties.HasFlag(ResettableProperties.TargetPosition))
            {
                TargetPosition = resetTargetPosition;
            }

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

            if (resetProperties.HasFlag(ResettableProperties.TargetPosition))
            {
                TargetPosition = resetTargetPosition;
            }

            UpdateAtTargetPosition();
        }

        private void OnMotionEvent(MotionEventType eventType)
        {
            switch (eventType)
            {
                case MotionEventType.Steady:
                    SetMotorState(MotorState.On);
                    IsAccelerating = false;
                    IsDecelerating = false;
                    AtSpeed = true;
                    IsMoving = true;
                    break;
                case MotionEventType.Decelerate:
                    SetMotorState(MotorState.Off);
                    IsAccelerating = false;
                    IsDecelerating = true;
                    AtSpeed = false;
                    IsMoving = true;
                    break;
                case MotionEventType.Stop:
                    SetMotorState(MotorState.Off);
                    UpdateAtTargetPosition();
                    IsAccelerating = false;
                    IsDecelerating = false;
                    AtSpeed = false;
                    IsMoving = false;
                    break;
                case MotionEventType.Accelerate:
                    SetMotorState(MotorState.On);
                    IsAccelerating = true;
                    IsDecelerating = false;
                    AtSpeed = false;
                    IsMoving = true;
                    break;
                case MotionEventType.Start:
                    SetMotorState(MotorState.On);
                    IsMoving = true;
                    break;
                case MotionEventType.ChangeDirectionForwards:
                    SetMotorDirection(MotorDirection.Forwards);
                    UpdateIsForwardsIsReverse();
                    IsMoving = true;
                    break;
                case MotionEventType.ChangeDirectionReverse:
                    SetMotorDirection(MotorDirection.Reverse);
                    UpdateIsForwardsIsReverse();
                    IsMoving = true;
                    break;
            }
        }
    }
}
