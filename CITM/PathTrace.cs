using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Xml.Linq;
using System.Xml.Serialization;

using Microsoft.DirectX;

using Demo3D.Native;
using Demo3D.PLC;
using Demo3D.Utilities;
using Demo3D.Common;
using Demo3D.Visuals;
using Demo3D.Components.Properties;

namespace Demo3D.Components
{
    [Resources(typeof(Resources))]
    [Category(nameof(Resources.Visualization_Category))]
    [HelpUrl("path_trace")]
    public class PathTrace : ExportableVisualAspect, IBindableItemOwner
    {
        private bool started = false;
        private int traceCount = 0;
        private int lineCount = 0;
        private Vector3 lastPosition = Vector3.Empty;
        private Visual traceVisual = null;
        private bool startTrace = false;
        private bool deleteTraceOnReset = false;
        private TimeProperty traceRate = 0.05;
        private double lineWidth = 2.0;
        private Color lineColor = Color.Magenta;

        private BindableItem<bool> startTraceBindableItem;

        [Description("Start Path Trace")]
        public bool StartTrace
        {
            get { return startTrace; }
            set
            {
                if (StartTraceBindableItem != null)
                {
                    // use bindable item to update value
                    if (StartTraceBindableItem.ValueAs<bool>() != value)
                    {
                        StartTraceBindableItem.Value = value;
                    }
                }
                else
                {
                    // call function to update value
                    SetStartTrace(value);
                }
            }
        }        

        [Browsable(false), XmlIgnore]
        public BindableItem<bool> StartTraceBindableItem
        {
            get { return startTraceBindableItem; }
            private set
            {
                if (startTraceBindableItem != value)
                {
                    var startTrace = StartTrace;
                    // detach old bindable item
                    if (startTraceBindableItem != null)
                    {
                        startTraceBindableItem.ValueChanged -= OnStartTraceBindableItemChanged;
                        startTraceBindableItem.DetachFromVisual();
                        startTraceBindableItem = null;
                    }
                    // attach new bindable item
                    startTraceBindableItem = value;
                    if (startTraceBindableItem != null)
                    {
                        startTraceBindableItem.ValueChanged += OnStartTraceBindableItemChanged;
                        startTraceBindableItem.DefaultAccess = PLC.Comms.AccessRights.ReadFromPLC;
                        startTraceBindableItem.IsBindingInterface = TriStateYNM.Yes;
                        startTraceBindableItem.Value = startTrace;
                    }
                }
            }
        }

        private void OnStartTraceBindableItemChanged(BindableItem item)
        {
            // call function to update value
            SetStartTrace(item.ValueAs<bool>());
        }

        private void SetStartTrace(bool value)
        {
            // don't execute this function until deserialization has completed
            if (this.Initializing) { return; }

            if (this.startTrace != value)
            {
                // update value
                this.startTrace = value;
                RaisePropertyChanged(nameof(StartTrace));
                // attempt to start path tracing if enabled
                if (this.startTrace)
                {
                    document.Run(() => PathTracingLoop());
                }
            }
        }

        [Description("Delete Path Trace On Reset")]
        public bool DeleteTraceOnReset
        {
            get { return deleteTraceOnReset; }
            set { deleteTraceOnReset = value; }
        }

        [Description("Path Trace Rate")]
        public TimeProperty TraceRate
        {
            get { return traceRate; }
            set
            {
                // don't execute this function until deserialization has completed
                if (this.Initializing) { return; }
                // error check trace rate (minimum of smaller of physics time step and mechanism time step)
                var minTimeStep = Math.Min(document.Scene.PhysicsTimeStep, document.Scene.MechanismsTimeStep);
                if (value < minTimeStep)
                {
                    traceRate = minTimeStep;
                }
                else
                {
                    traceRate = value;
                }
            }
        }        

        [Description("Line Width")]
        public double LineWidth
        {
            get { return lineWidth; }
            set { lineWidth = value; }
        }

        [Description("Line Color")]
        public Color LineColor
        {
            get { return lineColor; }
            set { lineColor = value; }
        }

        [Browsable(false)]
        public IEnumerable<BindableItem> BindableItems
        {
            get
            {
                // must return the collection of bindable items
                if (StartTraceBindableItem != null) { yield return StartTraceBindableItem; }
            }
        }

        private void CreateBindings()
        {
            // create bindings to bindable items
            StartTraceBindableItem = new ReadFromServer<bool>(Visual, BindingName(nameof(StartTrace)));
            UpdateBindingAPI();
        }

        private void RemoveBindings()
        {
            // remove bindings to bindable items
            StartTraceBindableItem = null;
            ReleaseBindingName(nameof(StartTrace));
            UpdateBindingAPI();
        }

        protected override void OnAssigned()
        {
            // called when the aspect has been reassigned to a visual or after the aspect has been removed
            base.OnAssigned();
            CleanupBindingAPI();
            if (Visual != null)
            {
                // create bindings
                CreateBindings();
            }
            else
            {
                // remove bindings
                RemoveBindings();
            }
        }

        protected override void OnRemoved()
        {
            base.OnRemoved();
            // merge trace visual
            MergeTraceVisual();
        }

        protected override void OnReset()
        {
            base.OnReset();
            // reset values
            StartTrace = false;
            started = false;
            // merge trace visual
            MergeTraceVisual();
        }

        private IEnumerable PathTracingLoop()
        {
            if (StartTrace && !started)
            {
                // latch started
                started = true;
                // create new trace visual
                traceVisual = document.CreateVisual<Visual>();
                traceCount++;
                traceVisual.Name = "PathTrace" + traceCount + "_" + Visual.Name;
                traceVisual.Type = "PathTraceVisual";
                lineCount = 0;
                // create lines while tracing enabled
                while (StartTrace && !Visual.IsDeleted())
                {
                    // update last position
                    lastPosition = Visual.WorldLocation;
                    // wait trace rate time
                    yield return Wait.ForSeconds(TraceRate);
                    // only create a new line if the visual has moved
                    if (Visual != null && Visual.WorldLocation != lastPosition)
                    {
                        // create new line
                        var line = Demo3D.Visuals.DrawingBlockVisual.CreateLine(document, lastPosition, Visual.WorldLocation, LineWidth, LineColor);
                        lineCount++;
                        line.Name = "Line" + lineCount;
                        line.Parent = traceVisual;
                        line.Type = "PathTraceLine";
                        line.SelectParentWhenPicked = true;
                        line.Draggable = false;
                    }
                }
                // unlatch started
                started = false;
                // merge trace visual
                MergeTraceVisual();
            }
        }

        private void MergeTraceVisual()
        {
            if (traceVisual != null)
            {
                // attempt to merge trace visual into a single drawing block visual
                var toMerge = new ArrayList();
                toMerge.Add(traceVisual);
                var mergedTraceVisual = MergeVisuals.Merge(toMerge) as DrawingBlockVisual;
                if (mergedTraceVisual != null)
                {
                    // set layer for drawing block visual
                    mergedTraceVisual.Layer = new LayerReference("Path Traces");
                    if (DeleteTraceOnReset)
                    {
                        // add reset listener to delete merged trace visual
                        mergedTraceVisual.ResetListeners -= MergedTraceVisual_ResetListeners;
                        mergedTraceVisual.ResetListeners += MergedTraceVisual_ResetListeners;
                    }
                }
                // delete original trace visual
                traceVisual.Delete();
                traceVisual = null;
            }
        }

        private void MergedTraceVisual_ResetListeners(Visual visual)
        {
            if (!visual.IsDeleted())
            {
                // delete merged trace visual
                visual.Delete();
            }
        }
    }
}