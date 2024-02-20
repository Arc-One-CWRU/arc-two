using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.ComponentModel;
using System.Xml.Serialization;

using Demo3D.Common;
using Demo3D.Utilities;
using Demo3D.PLC;
using Demo3D.PLC.Comms;
using Demo3D.Gui.AspectViewer;
using Demo3D.Gui.AspectViewer.Editors;
using Demo3D.EventQueue;
using Demo3D.Visuals;
using Demo3D.Visuals.Motor;

using KJEUtil = Demo3D.Visuals.KJE.Utilities;

namespace Demo3D.Components
{
    using Properties;

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.Motor_Controllers_Category))]
    [HelpUrl("position_controller")]
    public sealed class PositionController : ExportableVisualAspect, IController, IBindableItemOwner
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
            TargetPosition = 1 << 0
        }

        [Flags]
        public enum Output
        {
            None = 0,
            AtTargetPosition = 1 << 0
        }

        private MotorAspect motor;
        private Output outputs;
        private TargetType targetType;
        private double targetPosition;
        private bool atTargetPosition;
        private double resetTargetPosition;
        private ResettableProperties resetProperties;

        private BindableItem<double> targetPositionBindableItem;
        private BindableItem<bool> atTargetPositionBindableItem;

        private MotorAspect.Lock<MotorState> stateLock;
        private MotorAspect.Lock<MotorDirection> directionLock;

        private MotionPrompt prompt;

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

        [AspectProperty]
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
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool OutputAtTargetPosition
        {
            get { return outputs.HasFlag(Output.AtTargetPosition); }
        }

        [AspectProperty(IsVisible = false)]
        public UnitsAttribute PositionUnits
        {
            get { return (motor != null) ? motor.PositionUnits : null; }
        }

        [AspectProperty(IsVisible = false)]
        public bool IsAngular
        {
            get { return PositionUnits is AngleAttribute; }
        }

        [AspectProperty]
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

        [AspectProperty]
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

        [AspectProperty]
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

        [AspectProperty]
        [DefaultValue(ResettableProperties.None)]
        public ResettableProperties ResetProperties
        {
            get { return resetProperties; }
            set
            {
                if (SetProperty(ref resetProperties, value))
                {
                    RaisePropertyChanged(nameof(DoesResetTargetPosition));
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool DoesResetTargetPosition
        {
            get { return resetProperties.HasFlag(ResettableProperties.TargetPosition); }
        }

        [AspectProperty]
        [DefaultValue(0.0)]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(PositionUnits), IsVisiblePropertyLink = nameof(DoesResetTargetPosition))]
        public double ResetTargetPosition
        {
            get { return resetTargetPosition; }
            set { SetProperty(ref resetTargetPosition, value); }
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
        public IEnumerable<BindableItem> BindableItems
        {
            get
            {
                if (TargetPositionBindableItem != null) { yield return TargetPositionBindableItem; }
                if (AtTargetPositionBindableItem != null) { yield return AtTargetPositionBindableItem; }
            }
        }

        public PositionController()
        {
            motor = null;
            outputs = Output.None;
            targetPosition = 0.0;
            atTargetPosition = false;
            resetTargetPosition = 0.0;
            resetProperties = ResettableProperties.None;

            targetPositionBindableItem = null;
            atTargetPositionBindableItem = null;

            stateLock = null;
            directionLock = null;

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

            UpdatePrompt();
            UpdateAtTargetPosition();
        }

        protected override void OnReset()
        {
            base.OnReset();

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
                        RaisePropertyChanged(nameof(IsAngular));
                        UpdatePrompt();
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
            AtTargetPositionBindableItem = new WriteToServer<bool>(Visual, BindingName(nameof(AtTargetPosition)));

            UpdateBindings();
        }

        private void RemoveBindings()
        {
            TargetPositionBindableItem = null;
            AtTargetPositionBindableItem = null;

            ReleaseBindingName(nameof(TargetPosition));
            ReleaseBindingName(nameof(AtTargetPosition));

            UpdateBindingAPI();
        }

        private void UpdateBindings()
        {
            if (TargetPositionBindableItem != null) { TargetPositionBindableItem.IsBindingInterface = TriStateYNM.Yes; }
            if (AtTargetPositionBindableItem != null) { AtTargetPositionBindableItem.IsBindingInterface = Outputs.HasFlag(Output.AtTargetPosition) ? TriStateYNM.Yes : TriStateYNM.No; }

            UpdateBindingAPI();
        }

        private void UnlockProperties()
        {
            stateLock?.Dispose();
            stateLock = null;

            directionLock?.Dispose();
            directionLock = null;
        }

        private bool TryLockProperties()
        {
            if (stateLock == null) { stateLock = this.motor.TryLockTargetState(); }
            if (directionLock == null) { directionLock = this.motor.TryLockTargetDirection(); }

            if (stateLock == null || directionLock == null)
            {
                UnlockProperties();
                return false;
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
            
            var currentState = motor.Current();
            var currentPosition = motor.Current().Position;

            double signedDisplacement = targetPosition - currentPosition;
            if (targetType == TargetType.Continuous && IsAngular)
            {
                signedDisplacement = KJEUtil.NormalizePlusMinusOneEighty(targetPosition - currentPosition);
            }

            var targetDirection = (signedDisplacement >= 0.0) ? MotorDirection.Forwards : MotorDirection.Reverse;
            var motorDirection = (motor.Direction == MotorDirection.Forwards) ? MotionDirection.Forwards : MotionDirection.Reverse;
            var currentDirection = (currentState.Velocity == 0.0) ? motorDirection : ((currentState.Velocity >= 0.0) ? MotionDirection.Forwards : MotionDirection.Reverse);
            var currentSpeed = Math.Abs(currentState.Velocity);

            var motionProfile = new MotionProfile(currentDirection, currentSpeed, motor.TargetSpeed, motor.MaxAcceleration, motor.MaxDeceleration);

            if (prompt.Update(motionProfile, signedDisplacement))
            {
                // Force state on initially, since it may have been switched off by a previous event
                // but the motor may still be moving.
                SetMotorState(MotorState.On);
            }
            else
            {
                // Just switch the motor off, since it could be that the motor is currently switched
                // on but the speed is zero (D-8806).
                SetMotorState(MotorState.Off);
            }
        }

        private void OnMotionEvent(MotionEventType eventType)
        {
            switch (eventType)
            {
                case MotionEventType.Steady:
                    SetMotorState(MotorState.On);
                    break;
                case MotionEventType.Decelerate:
                    SetMotorState(MotorState.Off);
                    break;
                case MotionEventType.Stop:
                    SetMotorState(MotorState.Off);
                    UpdateAtTargetPosition();
                    break;
                case MotionEventType.Accelerate:
                case MotionEventType.Start:
                    SetMotorState(MotorState.On);
                    break;
                case MotionEventType.ChangeDirectionForwards:
                    SetMotorDirection(MotorDirection.Forwards);
                    break;
                case MotionEventType.ChangeDirectionReverse:
                    SetMotorDirection(MotorDirection.Reverse);
                    break;
            }
        }

        private void UpdateAtTargetPosition()
        {
            if (motor != null)
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

            AtTargetPosition = false;
        }

        private void SetMotorState(MotorState newState)
        {
            stateLock.Set(newState);
        }

        private void SetMotorDirection(MotorDirection newDirection)
        {
            directionLock.Set(newDirection);
        }

        private void OnTargetPositionBindableItemChanged(BindableItem item)
        {
            TargetPosition = item.ValueAs<double>();
        }

        private void OnAtTargetPositionBindableItemChanged(BindableItem item)
        {
            AtTargetPosition = item.ValueAs<bool>();
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

            UpdateAtTargetPosition();
        }
    }
}
