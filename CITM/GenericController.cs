using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Xml.Serialization;

using Demo3D.Gui.AspectViewer;
using Demo3D.Gui.AspectViewer.Editors;
using Demo3D.PLC;
using Demo3D.PLC.Comms;
using Demo3D.Utilities;
using Demo3D.Common;
using Demo3D.Visuals;
using Demo3D.Visuals.Motor;

namespace Demo3D.Components
{
    using Properties;

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.Motor_Controllers_Category))]
    [HelpUrl("generic_controller")]
    public sealed class GenericController : ExportableVisualAspect, IController, IBindableItemOwner
    {
        [Flags]
        public enum Input
        {
            None = 0,
            State = 1 << 0,
            Direction = 1 << 1,
            Speed = 1 << 2,
            Acceleration = 1 << 3,
        }

        private MotorAspect motor;

        private Input inputs;
        private bool state;
        private bool forwards;
        private bool reverse;
        private double speed;
        private double acceleration;
        private double deceleration;

        private MotorAspect.Lock<MotorState> stateLock;
        private MotorAspect.Lock<MotorDirection> directionLock;
        private MotorAspect.Lock<double> speedLock;
        private MotorAspect.Lock<double> accelerationLock;
        private MotorAspect.Lock<double> decelerationLock;

        private BindableItem<bool> stateBindableItem;
        private BindableItem<bool> forwardsBindableItem;
        private BindableItem<bool> reverseBindableItem;
        private BindableItem<double> speedBindableItem;
        private BindableItem<double> accelerationBindableItem;
        private BindableItem<double> decelerationBindableItem;

        [AspectProperty(IsVisible = true)]
        public override string Name
        {
            get => base.Name;
            set => base.Name = value;
        }

        [AspectProperty]
        public MotorAspect Motor
        {
            get { return motor; }
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

        [AspectProperty(IsVisible = true)]
        [DefaultValue(Input.None)]
        public Input Inputs
        {
            get { return inputs; }
            set
            {
                if (SetProperty(ref inputs, value))
                {
                    UnlockProperties();
                    if (motor != null && TryLockProperties() == false)
                    {
                        SetProperty(ref inputs, Input.None);
                    }

                    UpdateBindings();

                    RaisePropertyChanged(nameof(InputState));
                    RaisePropertyChanged(nameof(InputDirection));
                    RaisePropertyChanged(nameof(InputSpeed));
                    RaisePropertyChanged(nameof(InputAcceleration));
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool InputState
        {
            get { return inputs.HasFlag(Input.State); }
        }

        [AspectProperty(IsVisible = false)]
        public bool InputDirection
        {
            get { return inputs.HasFlag(Input.Direction); }
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

        [AspectProperty]
        [AspectEditorAttribute(IsVisiblePropertyLink = nameof(InputState))]
        [DefaultValue(false)]
        [Exportable(false)]
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
                else
                {
                    SetState(value);
                }
            }
        }

        [AspectProperty]
        [AspectEditorAttribute(IsVisiblePropertyLink = nameof(InputDirection))]
        [DefaultValue(true)]
        [Exportable(false)]
        public bool Forwards
        {
            get { return forwards; }
            set
            {
                if (ForwardsBindableItem != null)
                {
                    if (ForwardsBindableItem.ValueAs<bool>() != value)
                    {
                        ForwardsBindableItem.Value = value;
                    }
                }
                else
                {
                    SetForwards(value);
                }
            }
        }

        [AspectProperty]
        [AspectEditorAttribute(IsVisiblePropertyLink = nameof(InputDirection))]
        [DefaultValue(false)]
        [Exportable(false)]
        public bool Reverse
        {
            get { return reverse; }
            set
            {
                if (ReverseBindableItem != null)
                {
                    if (ReverseBindableItem.ValueAs<bool>() != value)
                    {
                        ReverseBindableItem.Value = value;
                    }
                }
                else
                {
                    SetReverse(value);
                }
            }
        }

        [AspectProperty]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(SpeedUnits), IsVisiblePropertyLink = nameof(InputSpeed))]
        [DefaultValue(0.0)]
        [Exportable(false)]
        public double Speed
        {
            get { return speed; }
            set
            {
                if (SpeedBindableItem != null)
                {
                    SpeedBindableItem.Value = value;
                    RaisePropertyChanged(nameof(Speed));
                }
                else
                {
                    SetSpeed(value);
                }
            }
        }

        [AspectProperty]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(AccelerationUnits), IsVisiblePropertyLink = nameof(InputAcceleration))]
        [DefaultValue(0.0)]
        [Exportable(false)]
        public double Acceleration
        {
            get { return acceleration; }
            set
            {
                if (AccelerationBindableItem != null)
                {
                    AccelerationBindableItem.Value = value;
                    RaisePropertyChanged(nameof(Acceleration));
                }
                else
                {
                    SetAcceleration(value);
                }
            }
        }

        [AspectProperty]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(AccelerationUnits), IsVisiblePropertyLink = nameof(InputAcceleration))]
        [DefaultValue(0.0)]
        [Exportable(false)]
        public double Deceleration
        {
            get { return deceleration; }
            set
            {
                if (DecelerationBindableItem != null)
                {
                    DecelerationBindableItem.Value = value;
                    RaisePropertyChanged(nameof(Deceleration));
                }
                else
                {
                    SetDeceleration(value);
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
        public BindableItem<bool> ForwardsBindableItem
        {
            get { return forwardsBindableItem; }
            private set
            {
                if (forwardsBindableItem != value)
                {
                    var forwards = Forwards;

                    if (forwardsBindableItem != null)
                    {
                        forwardsBindableItem.ValueChanged -= OnForwardsBindableItemChanged;
                        forwardsBindableItem.DetachFromVisual();
                        forwardsBindableItem = null;
                    }

                    forwardsBindableItem = value;
                    if (forwardsBindableItem != null)
                    {
                        forwardsBindableItem.ValueChanged += OnForwardsBindableItemChanged;
                        forwardsBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
                        forwardsBindableItem.Value = forwards;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<bool> ReverseBindableItem
        {
            get { return reverseBindableItem; }
            private set
            {
                if (reverseBindableItem != value)
                {
                    var reverse = Reverse;

                    if (reverseBindableItem != null)
                    {
                        reverseBindableItem.ValueChanged -= OnReverseBindableItemChanged;
                        reverseBindableItem.DetachFromVisual();
                        reverseBindableItem = null;
                    }

                    reverseBindableItem = value;
                    if (reverseBindableItem != null)
                    {
                        reverseBindableItem.ValueChanged += OnReverseBindableItemChanged;
                        reverseBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
                        reverseBindableItem.Value = reverse;
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
        public IEnumerable<BindableItem> BindableItems
        {
            get
            {
                if (StateBindableItem != null) { yield return StateBindableItem; }
                if (ForwardsBindableItem != null) { yield return ForwardsBindableItem; }
                if (ReverseBindableItem != null) { yield return ReverseBindableItem; }
                if (SpeedBindableItem != null) { yield return SpeedBindableItem; }
                if (AccelerationBindableItem != null) { yield return AccelerationBindableItem; }
                if (DecelerationBindableItem != null) { yield return DecelerationBindableItem; }
            }
        }

        public GenericController()
        {
            motor = null;

            inputs = Input.None;
            state = false;
            forwards = true;
            reverse = false;
            speed = 0.0;
            acceleration = 0.0;
            deceleration = 0.0;

            stateLock = null;
            directionLock = null;
            speedLock = null;
            accelerationLock = null;
            decelerationLock = null;

            stateBindableItem = null;
            forwardsBindableItem = null;
            reverseBindableItem = null;
            speedBindableItem = null;
            accelerationBindableItem = null;
            decelerationBindableItem = null;
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

            base.OnRemoved();
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();

            // The motor's target properties may get reset when the model gets reset, so we need to
            // update the target properties when the model is initialized/ 
            UpdateMotorProperties();
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
                        UpdateMotorProperties(); // Set properties on the motor.
                        Subscribe(); // Subscribe for events on the motor.
                        RaisePropertyChanged(nameof(SpeedUnits)); // Update the units displayed in the GUI.
                        RaisePropertyChanged(nameof(AccelerationUnits));
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
                RaisePropertyChanged(nameof(SpeedUnits));
                RaisePropertyChanged(nameof(AccelerationUnits));
            }
        }

        public void Pause()
        {
            // Nothing to do.
        }

        public void Resume(double pauseDuration)
        {
            // Nothing to do.
        }

        private void CreateBindings()
        {
            Debug.Assert(Visual != null);

            StateBindableItem = new ReadFromServer<bool>(Visual, BindingName(nameof(State)));
            ForwardsBindableItem = new ReadFromServer<bool>(Visual, BindingName(nameof(Forwards)));
            ReverseBindableItem = new ReadFromServer<bool>(Visual, BindingName(nameof(Reverse)));
            SpeedBindableItem = new ReadFromServer<double>(Visual, BindingName(nameof(Speed)));
            AccelerationBindableItem = new ReadFromServer<double>(Visual, BindingName(nameof(Acceleration)));
            DecelerationBindableItem = new ReadFromServer<double>(Visual, BindingName(nameof(Deceleration)));

            UpdateBindings();
        }

        private void RemoveBindings()
        {
            StateBindableItem = null;
            ForwardsBindableItem = null;
            ReverseBindableItem = null;
            SpeedBindableItem = null;
            AccelerationBindableItem = null;
            DecelerationBindableItem = null;

            ReleaseBindingName(nameof(State));
            ReleaseBindingName(nameof(Forwards));
            ReleaseBindingName(nameof(Reverse));
            ReleaseBindingName(nameof(Speed));
            ReleaseBindingName(nameof(Acceleration));
            ReleaseBindingName(nameof(Deceleration));

            UpdateBindingAPI();
        }

        private void UpdateBindings()
        {
            if (StateBindableItem != null) { StateBindableItem.IsBindingInterface = inputs.HasFlag(Input.State) ? TriStateYNM.Yes : TriStateYNM.No; }
            if (ForwardsBindableItem != null) { ForwardsBindableItem.IsBindingInterface = inputs.HasFlag(Input.Direction) ? TriStateYNM.Yes : TriStateYNM.No; }
            if (ReverseBindableItem != null) { ReverseBindableItem.IsBindingInterface = inputs.HasFlag(Input.Direction) ? TriStateYNM.Yes : TriStateYNM.No; }
            if (SpeedBindableItem != null) { SpeedBindableItem.IsBindingInterface = inputs.HasFlag(Input.Speed) ? TriStateYNM.Yes : TriStateYNM.No; }
            if (AccelerationBindableItem != null) { AccelerationBindableItem.IsBindingInterface = inputs.HasFlag(Input.Acceleration) ? TriStateYNM.Yes : TriStateYNM.No; }
            if (DecelerationBindableItem != null) { DecelerationBindableItem.IsBindingInterface = inputs.HasFlag(Input.Acceleration) ? TriStateYNM.Yes : TriStateYNM.No; }

            UpdateBindingAPI();
        }

        public bool TryLockProperties()
        {
            if (motor == null) { return false; }

            if (inputs.HasFlag(Input.State))
            {
                if (stateLock == null) { stateLock = motor.TryLockTargetState(); }
                if (stateLock == null) { UnlockProperties(); return false; }
            }

            if (inputs.HasFlag(Input.Direction) || inputs.HasFlag(Input.Speed)) // Support negative speeds.
            {
                if (directionLock == null) { directionLock = motor.TryLockTargetDirection(); }
                if (directionLock == null) { UnlockProperties(); return false; }
            }

            if (inputs.HasFlag(Input.Speed))
            {
                if (speedLock == null) { speedLock = motor.TryLockTargetSpeed(); }
                if (speedLock == null) { UnlockProperties(); return false; }
            }

            if (inputs.HasFlag(Input.Acceleration))
            {
                if (accelerationLock == null) { accelerationLock = motor.TryLockMaxAcceleration(); }
                if (decelerationLock == null) { decelerationLock = motor.TryLockMaxDeceleration(); }
                if (accelerationLock == null || decelerationLock == null) { UnlockProperties(); return false; }
            }

            return true;
        }

        private void UnlockProperties()
        {
            stateLock?.Dispose();
            directionLock?.Dispose();
            speedLock?.Dispose();
            accelerationLock?.Dispose();
            decelerationLock?.Dispose();

            stateLock = null;
            directionLock = null;
            speedLock = null;
            accelerationLock = null;
            decelerationLock = null;
        }

        private void Subscribe()
        {
            motor.OnUnitsChanged += OnUnitsChanged;
        }

        private void Unsubscribe()
        {
            motor.OnUnitsChanged -= OnUnitsChanged;
        }

        private void OnUnitsChanged(MotorAspect motor, MotorAspect.Units oldUnits, MotorAspect.Units newUnits)
        {
            RaisePropertyChanged(nameof(SpeedUnits));
            RaisePropertyChanged(nameof(AccelerationUnits));
        }

        void OnStateBindableItemChanged(BindableItem item)
        {
            SetState(item.ValueAs<bool>());
        }

        void OnForwardsBindableItemChanged(BindableItem item)
        {
            SetForwards(item.ValueAs<bool>());
        }

        void OnReverseBindableItemChanged(BindableItem item)
        {
            SetReverse(item.ValueAs<bool>());
        }

        void OnSpeedBindableItemChanged(BindableItem item)
        {
            SetSpeed(item.ValueAs<double>());
        }

        void OnAccelerationBindableItemChanged(BindableItem item)
        {
            SetAcceleration(item.ValueAs<double>());
        }

        void OnDecelerationBindableItemChanged(BindableItem item)
        {
            SetDeceleration(item.ValueAs<double>());
        }

        private void SetState(bool newState)
        {
            if (state != newState)
            {
                state = newState;
                UpdateMotorProperties();

                RaisePropertyChanged(nameof(State));
            }
        }

        private void SetForwards(bool newForwards)
        {
            if (forwards != newForwards)
            {
                forwards = newForwards;
                UpdateMotorProperties();

                RaisePropertyChanged(nameof(Forwards));
            }
        }

        private void SetReverse(bool newReverse)
        {
            if (reverse != newReverse)
            {
                reverse = newReverse;
                UpdateMotorProperties();

                RaisePropertyChanged(nameof(Reverse));
            }
        }

        private void SetSpeed(double newSpeed)
        {
            if (speed != newSpeed)
            {
                speed = newSpeed;
                UpdateMotorProperties();

                RaisePropertyChanged(nameof(Speed));
            }
        }

        private void SetAcceleration(double newAcceleration)
        {
            if (acceleration != newAcceleration)
            {
                acceleration = newAcceleration;
                UpdateMotorProperties();

                RaisePropertyChanged(nameof(Acceleration));
            }
        }

        private void SetDeceleration(double newDeceleration)
        {
            if (deceleration != newDeceleration)
            {
                deceleration = newDeceleration;
                UpdateMotorProperties();

                RaisePropertyChanged(nameof(Deceleration));
            }
        }

        private void UpdateMotorProperties()
        {
            stateLock?.Set((state && (forwards ^ reverse)) ? MotorState.On : MotorState.Off);
            directionLock?.Set((forwards ^ (speed < 0.0)) ? MotorDirection.Forwards : MotorDirection.Reverse);
            speedLock?.Set(Math.Abs(speed));
            accelerationLock?.Set(acceleration);
            decelerationLock?.Set(deceleration);
        }
    }
}
