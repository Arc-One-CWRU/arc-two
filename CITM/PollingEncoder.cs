using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.ComponentModel;
using System.Xml.Serialization;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;

using Demo3D.PLC;
using Demo3D.PLC.Comms;
using Demo3D.EventQueue;
using Demo3D.Gui.AspectViewer;
using Demo3D.Gui.AspectViewer.Editors;
using Demo3D.Common;
using Demo3D.Utilities;
using Demo3D.Visuals;
using Demo3D.Visuals.Motor;

using KJEUtil = Demo3D.Visuals.KJE.Utilities;

namespace Demo3D.Components
{
    using Properties;

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.Motor_Encoders_Category))]
    [HelpUrl("polling_encoder")]
    public sealed class PollingEncoder : ExportableVisualAspect, IEncoder, IBindableItemOwner
    {
        [Flags]
        public enum Output
        {
            None = 0,
            Position = 1 << 0,
            Velocity = 1 << 1,
            Acceleration = 1 << 2
        }

        public enum AngleMode
        {
            Discontinuous,
            Continuous
        }

        private MotorAspect motor;
        private Output outputs;
        private bool rolloverEnabled;
        private double rolloverPosition;
        private double position;
        private double velocity;
        private double acceleration;
        private double frequency;
        private AngleMode angleType;
        private Event scheduledEvent;

        private BindableItem<double> positionBindableItem;
        private BindableItem<double> velocityBindableItem;
        private BindableItem<double> accelerationBindableItem;

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
        [Frequency]
        [DefaultValue(10.0)]
        public double Frequency
        {
            get { return frequency; }
            set
            {
                var clampedValue = Math.Min(100.0, Math.Max(0.0, value));
                if (SetProperty(ref frequency, clampedValue))
                {
                    ReschedulePolling();

                    if (clampedValue != value)
                    {
                        RaisePropertyChanged(nameof(Frequency));
                    }
                }
            }
        }

        [AspectProperty(IsVisible = true)]
        [DefaultValue(Output.None)]
        public Output Outputs
        {
            get { return outputs; }
            set
            {
                if (SetProperty(ref outputs, value))
                {
                    UpdateBindings();

                    RaisePropertyChanged(nameof(OutputPosition));
                    RaisePropertyChanged(nameof(OutputVelocity));
                    RaisePropertyChanged(nameof(OutputAcceleration));
                    RaisePropertyChanged(nameof(OutputAngle));
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool OutputPosition
        {
            get { return outputs.HasFlag(Output.Position); }
        }

        [AspectProperty(IsVisible = false)]
        public bool OutputVelocity
        {
            get { return outputs.HasFlag(Output.Velocity); }
        }

        [AspectProperty(IsVisible = false)]
        public bool OutputAcceleration
        {
            get { return outputs.HasFlag(Output.Acceleration); }
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
        public bool OutputAngle
        {
            get { return OutputPosition && PositionUnits is AngleAttribute; }
        }

        [AspectProperty(IsVisible = false)]
        public bool RolloverPositionVisible
        {
            get { return OutputPosition && RolloverEnabled; }
        }

        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(OutputAngle), IsEnabledPropertyLink = nameof(OutputAngle))]
        [DefaultValue(AngleMode.Discontinuous)]
        public AngleMode AngleType
        {
            get { return angleType; }
            set { SetProperty(ref angleType, value); }
        }

        [AspectProperty(IsVisible = true)]
        [AspectEditor(IsVisiblePropertyLink = nameof(OutputPosition))]
        [DefaultValue(false)]
        public bool RolloverEnabled
        {
            get { return rolloverEnabled; }
            set
            {
                if (SetProperty(ref rolloverEnabled, value))
                {
                    RaisePropertyChanged(nameof(RolloverPosition));
                }
            }
        }

        [AspectProperty]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(PositionUnits), IsVisiblePropertyLink = nameof(RolloverPositionVisible))]
        [XmlIgnore]
        [Description("Rollover Position (must be > 0.0 to rollover)")]
        [DefaultValue(0.0)]
        public double RolloverPosition
        {
            get { return rolloverPosition; }
            set
            {
                if (value <= 0.0)
                {
                    value = 0.0;
                }
                if (AngleType == AngleMode.Continuous && OutputAngle)
                {
                    value = KJEUtil.NormalizeZeroThreeSixty(value);
                }
                SetProperty(ref rolloverPosition, value);
                RaisePropertyChanged(nameof(RolloverPosition));
            }
        }

        [AspectProperty]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(PositionUnits), IsVisiblePropertyLink = nameof(OutputPosition))]
        [XmlIgnore]
        [DefaultValue(0.0)]
        public double Position
        {
            get { return position; }
            private set
            {
                if (AngleType == AngleMode.Continuous && OutputAngle)
                {
                    value = KJEUtil.NormalizeZeroThreeSixty(value);
                }

                if (SetProperty(ref position, value))
                {
                    if (PositionBindableItem != null)
                    {
                        PositionBindableItem.Value = position;
                    }
                }
            }
        }

        [AspectProperty]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(SpeedUnits), IsVisiblePropertyLink = nameof(OutputVelocity))]
        [XmlIgnore]
        [DefaultValue(0.0)]
        public double Velocity
        {
            get { return velocity; }
            private set
            {
                if (SetProperty(ref velocity, value))
                {
                    if (VelocityBindableItem != null)
                    {
                        VelocityBindableItem.Value = velocity;
                    }
                }
            }
        }

        [AspectProperty]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(AccelerationUnits), IsVisiblePropertyLink = nameof(OutputAcceleration))]
        [XmlIgnore]
        [DefaultValue(0.0)]
        public double Acceleration
        {
            get { return acceleration; }
            private set
            {
                if (SetProperty(ref acceleration, value))
                {
                    if (AccelerationBindableItem != null)
                    {
                        AccelerationBindableItem.Value = acceleration;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<double> PositionBindableItem
        {
            get { return positionBindableItem; }
            private set
            {
                if (positionBindableItem != value)
                {
                    positionBindableItem?.DetachFromVisual();
                    positionBindableItem = value;

                    if (positionBindableItem != null)
                    {
                        positionBindableItem.Value = Position;
                        positionBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<double> VelocityBindableItem
        {
            get { return velocityBindableItem; }
            private set
            {
                if (velocityBindableItem != value)
                {
                    velocityBindableItem?.DetachFromVisual();
                    velocityBindableItem = value;

                    if (velocityBindableItem != null)
                    {
                        velocityBindableItem.Value = Velocity;
                        velocityBindableItem.DefaultAccess = AccessRights.WriteToPLC;
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
                    accelerationBindableItem?.DetachFromVisual();
                    accelerationBindableItem = value;

                    if (accelerationBindableItem != null)
                    {
                        accelerationBindableItem.Value = Velocity;
                        accelerationBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public IEnumerable<BindableItem> BindableItems
        {
            get
            {
                if (PositionBindableItem != null) { yield return PositionBindableItem; }
                if (VelocityBindableItem != null) { yield return VelocityBindableItem; }
                if (AccelerationBindableItem != null) { yield return AccelerationBindableItem; }
            }
        }

        public PollingEncoder()
        {
            motor = null;
            outputs = Output.None;
            rolloverEnabled = false;
            rolloverPosition = 0.0;
            position = 0.0;
            velocity = 0.0;
            acceleration = 0.0;
            frequency = 10.0;
            angleType = AngleMode.Discontinuous;

            positionBindableItem = null;
            velocityBindableItem = null;
            accelerationBindableItem = null;
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
            CancelPolling();

            if (motor != null)
            {
                motor.Encoder = null; // Detach the encoder from the motor.
            }

            base.OnRemoved();
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();

            ReschedulePolling();
        }

        protected override void OnReset()
        {
            base.OnReset();

            CancelPolling();
        }

        public bool Attach(MotorAspect motor)
        {
            // If we are already attached to a motor, then the attachment should fail.
            if (this.motor == null && motor != null)
            {
                this.motor = motor;
                if (this.motor != null)
                {
                    // Attachment succeeded!
                    Poll(); // Poll now.
                    ReschedulePolling(); // Schedule polling.
                    Subscribe(); // Subscribe for events on the motor.
                    RaisePropertyChanged(nameof(PositionUnits)); // Update the units displayed in the GUI.
                    RaisePropertyChanged(nameof(SpeedUnits));
                    RaisePropertyChanged(nameof(AccelerationUnits));
                    RaisePropertyChanged(nameof(OutputAngle));
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
                CancelPolling();
                Unsubscribe();
                motor = null;
                position = 0.0;

                RaisePropertyChanged(nameof(Motor));
                RaisePropertyChanged(nameof(PositionUnits));
                RaisePropertyChanged(nameof(SpeedUnits));
                RaisePropertyChanged(nameof(AccelerationUnits));
                RaisePropertyChanged(nameof(OutputAngle));
            }
        }

        public void Pause()
        {
            CancelPolling(); // No point in polling whilst the motor is paused.
        }

        public void Resume(double pauseDuration)
        {
            PollAndSchedule(); // Resume polling once the motor is unpaused.
        }

        private void CreateBindings()
        {
            Debug.Assert(Visual != null);

            PositionBindableItem = new WriteToServer<double>(Visual, BindingName(nameof(Position)));
            VelocityBindableItem = new WriteToServer<double>(Visual, BindingName(nameof(Velocity)));
            AccelerationBindableItem = new WriteToServer<double>(Visual, BindingName(nameof(Acceleration)));

            UpdateBindings();
        }

        private void UpdateBindings()
        {
            if (PositionBindableItem != null) { PositionBindableItem.IsBindingInterface = Outputs.HasFlag(Output.Position) ? TriStateYNM.Yes : TriStateYNM.No; }
            if (VelocityBindableItem != null) { VelocityBindableItem.IsBindingInterface = Outputs.HasFlag(Output.Velocity) ? TriStateYNM.Yes : TriStateYNM.No; }
            if (AccelerationBindableItem != null) { AccelerationBindableItem.IsBindingInterface = Outputs.HasFlag(Output.Acceleration) ? TriStateYNM.Yes : TriStateYNM.No; }

            UpdateBindingAPI();
        }

        private void RemoveBindings()
        {
            PositionBindableItem = null;
            VelocityBindableItem = null;
            AccelerationBindableItem = null;

            ReleaseBindingName(nameof(Position));
            ReleaseBindingName(nameof(Velocity));
            ReleaseBindingName(nameof(Acceleration));

            UpdateBindingAPI();
        }

        private void Subscribe()
        {
            motor.OnAfterReset += OnAfterReset;
            motor.OnUnitsChanged += OnUnitsChanged;
        }

        private void Unsubscribe()
        {
            motor.OnAfterReset -= OnAfterReset;
            motor.OnUnitsChanged -= OnUnitsChanged;
        }

        private void OnAfterReset(MotorAspect motor)
        {
            Poll(); // Initial state may have been applied.
        }

        private void OnUnitsChanged(MotorAspect motor, MotorAspect.Units oldUnits, MotorAspect.Units newUnits)
        {
            RaisePropertyChanged(nameof(PositionUnits));
            RaisePropertyChanged(nameof(SpeedUnits));
            RaisePropertyChanged(nameof(AccelerationUnits));
            RaisePropertyChanged(nameof(OutputAngle));
        }

        private void ReschedulePolling()
        {
            if (Visual != null)
            {
                CancelPolling();
                if (frequency > 0.0)
                {
                    PollAndSchedule();
                }
            }
        }

        private void PollAndSchedule()
        {
            Poll();

            double seconds = 1.0 / frequency;
            var secondsAsFixed = Fixed.RoundUp(ref seconds);
            scheduledEvent = Visual.Document.EventQueue.ScheduleAction(secondsAsFixed, PollAndSchedule);
        }

        private void CancelPolling()
        {
            scheduledEvent?.Cancel();
            scheduledEvent = null;
        }

        private void Poll()
        {
            if (motor != null)
            {
                var current = motor.Current(); // Get the current state.

                Position = rolloverEnabled && RolloverPosition > 0.0 ? Modulo(current.Position, rolloverPosition) : current.Position;
                Velocity = current.Velocity;
                Acceleration = current.Acceleration;
            }
        }

        public double GetPosition()
        {
            Poll();
            return Position;
        }

        public double GetVelocity()
        {
            Poll();
            return Velocity;
        }

        public double GetAcceleration()
        {
            Poll();
            return Acceleration;
        }

        private static double Modulo(double value, double modulus)
        {
            // modulo function which also handles negative numbers
            double result = value % modulus;
            return result < 0 ? result + modulus : result;
        }
    }
}