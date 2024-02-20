using System;
using System.Linq;
using System.Diagnostics;
using System.Drawing;
using System.ComponentModel;
using System.Collections.Generic;

using Demo3D.Visuals;
using Demo3D.Common;
using Demo3D.Gui.AspectViewer;

namespace Demo3D.Components
{
    using Properties;

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.Sensors_Category))]
    [HelpUrl("diagnostic_sensor")]
    public class DiagnosticSensor : ExportableVisualAspect, ISensorAspect, IPhysicsGroupProvider
    {
        static readonly PropertyChangedEventArgs LabelsProperty = new PropertyChangedEventArgs(nameof(Labels));
        static readonly PropertyChangedEventArgs DetectProperty = new PropertyChangedEventArgs(nameof(Detect));
        static readonly PropertyChangedEventArgs BlockingVisualsProperty = new PropertyChangedEventArgs(nameof(BlockingVisuals));
        static readonly PropertyChangedEventArgs IsBlockedProperty = new PropertyChangedEventArgs(nameof(IsBlocked));

        private List<PreviewObject> previewObjects = null;
        private List<string> labels = null;
        private List<string> detect = null;

        private PhysicsEngine PhysicsEngine { get { return this.document.PhysicsEngine; } }

        PhysicsGroup IPhysicsGroupProvider.PhysicsGroup { get { return this.PhysicsEngine.DiagnosticSensorsGroup; } }

        [AspectProperty, DefaultValue("")]
        public string Labels
        {
            get
            {
                if (this.labels != null)
                {
                    return String.Join(",", this.labels);
                }

                return "";
            }

            set
            {
                if (value != null && String.IsNullOrWhiteSpace(value) == false)
                {
                    this.labels = value.Split(',').Select(l => l.Trim()).ToList();
                    if (this.labels.Count <= 0)
                    {
                        this.labels = null;
                    }

                    RaisePropertyChanged(LabelsProperty);
                }
            }
        }

        [AspectProperty, DefaultValue("*")]
        public string Detect
        {
            get
            {
                if (this.detect != null)
                {
                    return String.Join(",", this.detect);
                }

                return "*";
            }

            set
            {
                if (value != null && String.IsNullOrWhiteSpace(value) == false)
                {
                    this.detect = value.Split(',').Select(l => l.Trim()).ToList();
                    if (this.detect.Count <= 0)
                    {
                        this.detect = null;
                    }

                    RaisePropertyChanged(DetectProperty);
                }
            }
        }

        [AspectProperty, DefaultValue(true)]
        public bool HighlightBlockingVisuals { get; set; } = true;

        [AspectProperty, DefaultValue(false)]
        public bool StopOnBlocked { get; set; } = false;

        [AspectProperty, Exportable(false)]
        public bool IsBlocked
        {
            get { return this.BlockingVisuals.Count > 0; }
        }

        [AspectProperty, Exportable(false)]
        public VisualList BlockingVisuals { get; } = new VisualList();

        public event Action<Visual> OnBlocked;
        public event Action<Visual> OnCleared;

        private bool HasLabel(string label)
        {
            return (this.labels != null) ? this.labels.Contains(label) : false;
        }

        private bool BlockedBy(Visual otherVisual)
        {
            if (otherVisual != null)
            {
                return this.BlockedBy(otherVisual.FindAspect<DiagnosticSensor>());
            }

            return false;
        }

        private bool BlockedBy(DiagnosticSensor otherAspect)
        {
            if (otherAspect != null)
            {
                if (this.detect == null)
                {
                    return true; // Implicit wildcard.
                }
                else
                {
                    foreach (var label in this.detect)
                    {
                        if (label == "*") { return true; }
                        if (otherAspect.HasLabel(label))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private void AddBlockingVisual(Visual visual)
        {
            if (visual == null) { return; }

            if (this.BlockingVisuals.Contains(visual) == false)
            {
                int previousCount = this.BlockingVisuals.Count;
                this.BlockingVisuals.Add(visual);

                if (previousCount <= 0 && this.BlockingVisuals.Count > 0)
                {
                    RaisePropertyChanged(IsBlockedProperty);
                }

                RaisePropertyChanged(BlockingVisualsProperty);

                this.OnBlocked?.Invoke(visual);

                this.app.LogMessage("Info", $"Diagnostic Sensor: {this.Visual.Name} was blocked by {visual.Name}.", this.Visual);

                if (this.StopOnBlocked)
                {
                    this.app.StopRunning();
                    this.app.FocusWithoutSelect(visual, false);
                }
            }
        }

        private void RemoveBlockingVisual(Visual visual)
        {
            int previousCount = this.BlockingVisuals.Count;
            if (this.BlockingVisuals.Remove(visual))
            {
                if (this.BlockingVisuals.Count <= 0)
                {
                    RaisePropertyChanged(IsBlockedProperty);
                }

                RaisePropertyChanged(BlockingVisualsProperty);

                this.OnCleared?.Invoke(visual);
            }
        }

        private void Highlight(Visual visual)
        {
            if (visual != null)
            {
                if (this.previewObjects == null)
                {
                    this.previewObjects = new List<PreviewObject>();
                }

                this.previewObjects.Add(new PreviewObject(visual, Color.Red, true));
            }
        }

        private void ClearHighlights()
        {
            if (this.previewObjects != null)
            {
                foreach (var previewObject in this.previewObjects)
                {
                    previewObject.Persistent = false;
                }

                this.previewObjects.Clear();
                this.previewObjects = null;
            }
        }

        private void BuilderTool_PreRenderListeners(object sender, EventArgs e)
        {
            this.ClearHighlights();

            if (this.HighlightBlockingVisuals)
            {
                foreach (var visualReference in this.BlockingVisuals)
                {
                    if (visualReference != null)
                    {
                        this.Highlight(visualReference.Visual);
                    }
                }

                if (this.previewObjects != null)
                {
                    foreach (var previewObject in this.previewObjects)
                    {
                        this.app.BuilderTool.Preview(previewObject);
                    }
                }
            }
        }

        private bool OnProcessCollision(PhysicsCollision collision, Visual otherVisual, PhysicsBody otherBody)
        {
            if (collision.HasPenetratingContacts())
            {
                if (this.PhysicsEngine.IsFirstContact(this.Visual, otherVisual))
                {
                    if (this.BlockedBy(otherVisual))
                    {
                        this.PhysicsEngine.AddPostCollideDelegate(this.PostCollide_OnBlocked, otherVisual);
                    }

                    var otherAspect = otherVisual.FindAspect<DiagnosticSensor>();
                    if (otherAspect.BlockedBy(this))
                    {
                        this.PhysicsEngine.AddPostCollideDelegate(otherAspect.PostCollide_OnBlocked, Visual);
                    }
                }
            }

            return true;
        }

        private void OnProcessCleared(Visual otherVisual)
        {
            this.PhysicsEngine.AddPostCollideDelegate(new PostCollideDelegate(this.PostCollide_OnCleared), otherVisual);

            var otherAspect = otherVisual.FindAspect<DiagnosticSensor>();
            if (otherAspect != null)
            {
                this.PhysicsEngine.AddPostCollideDelegate(new PostCollideDelegate(otherAspect.PostCollide_OnCleared), Visual);
            }
        }

        private void PostCollide_OnBlocked(Visual load)
        {
            Debug.Assert(load != null);
            this.AddBlockingVisual(load);
        }

        private void PostCollide_OnCleared(Visual load)
        {
            Debug.Assert(load != null);
            this.RemoveBlockingVisual(load);
        }

        protected override void OnInitialize()
        {
            this.app.BuilderTool.PreRenderListeners -= BuilderTool_PreRenderListeners;
            this.app.BuilderTool.PreRenderListeners += BuilderTool_PreRenderListeners;

            this.Visual.OnProcessCollision += this.OnProcessCollision;
            this.Visual.OnProcessCleared += this.OnProcessCleared;
        }

        protected override void OnReset()
        {
            base.OnReset();

            this.ClearHighlights();
            this.BlockingVisuals.Clear();

            this.app.BuilderTool.PreRenderListeners -= BuilderTool_PreRenderListeners;

            this.Visual.OnProcessCollision -= this.OnProcessCollision;
            this.Visual.OnProcessCleared -= this.OnProcessCleared;
        }

        protected override bool CanAdd(ref string reasonForFailure)
        {
            if (Visual is CoreVisual)
            {
                reasonForFailure = "diagnostic sensor aspects cannot be added to core visuals";
                return false;
            }

            var physicsGroups = Visual.FindAspects<IPhysicsGroupProvider>();
            if (physicsGroups != null)
            {
                foreach (var physicsGroup in physicsGroups)
                {
                    if (physicsGroup != this)
                    {
                        reasonForFailure = "visual already has a physics group provider aspect";
                        return false;
                    }
                }
            }

            return true;
        }

        protected override void OnAdded()
        {
            base.OnAdded();

            // Create bounding box physics unless there is already physics attached.
            var physicsGeometry = Visual.FindAspect<PhysicsGeometryAspect>();
            if (physicsGeometry == null)
            {
                physicsGeometry = Visual.FindCreateAspect<BoxPhysicsAspect>();
                physicsGeometry.AspectManagedBy = this;
            }
        }

        protected override void OnRemoved()
        {
            base.OnRemoved();

            this.ClearHighlights();
            this.BlockingVisuals.Clear();

            this.app.BuilderTool.PreRenderListeners -= BuilderTool_PreRenderListeners;

            this.Visual.OnProcessCollision -= this.OnProcessCollision;
            this.Visual.OnProcessCleared -= this.OnProcessCleared;
        }
    }
}
