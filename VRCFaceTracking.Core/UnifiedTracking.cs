using VRCFaceTracking.Core.Params;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;
using VRCFaceTracking.Core.Params.Expressions.Legacy.Eye;
using VRCFaceTracking.Core.Params.Expressions.Legacy.Lip;
using VRCFaceTracking.Core.Types;

namespace VRCFaceTracking
{
    /// <summary>
    /// Class that contains all relevant data
    /// </summary>
    public class UnifiedTracking
    {
        /// <summary>
        /// Eye image data sent from the loaded eye module.
        /// </summary>
        public static Image EyeImageData = new Image();

        /// <summary>
        /// Lip / Expression image data sent from the loaded expressions module.
        /// </summary>
        public static Image LipImageData = new Image();

        /// <summary>
        /// Latest Expression Data accessible and sent by all VRCFaceTracking modules.
        /// </summary>
        public static UnifiedTrackingData Data = new();

        /// <summary>
        /// Container of all features and functions that mutates the incoming expression data into output data suitable for driving Unified Expressions.
        /// </summary>
        /// <remarks> Mutates data on update. </remarks>
        public static UnifiedTrackingMutator Mutator = null!;

#pragma warning disable CS0618
        /// <summary>
        /// Version 1 (VRCFaceTracking SRanipal) of all accessible output parameters.
        /// </summary>
        /// <remarks> These parameters are going to be undocumented in the near future and are directly emulated by Version 2 (Unified Expressions) parameters. </remarks>
        public static readonly Parameter[] AllParameters_v1 = LipShapeMerger.AllLipParameters.Union(EyeTrackingParams.ParameterList).ToArray();

        /// <summary>
        /// Version 2 (Unified Expressions) of all accessible output parameters.
        /// </summary>
        public static readonly Parameter[] AllParameters_v2 = UnifiedExpressionsParameters.ExpressionParameters;

        /// <summary>
        /// Head tracking parameters
        /// </summary>
        public static readonly Parameter[] HeadParameters = UnifiedHeadParameters.HeadParameters;

        /// <summary> 
        /// The collection of EVERY possible output parameter
        /// </summary>
        public static readonly Parameter[] AllParameters = AllParameters_v2.Concat(AllParameters_v1).Concat(HeadParameters).ToArray();
#pragma warning restore CS0618

        /// <summary>
        /// Central update action for all expression data to subscribe to.
        /// </summary>
        public static Action<UnifiedTrackingData> OnUnifiedDataUpdated = OnUnifiedDataUpdated + (_ => { }) ?? (_ => {}); 

        /// <summary>
        /// Central update function that updates all output parameter data and pushes the latest expressions from VRCFaceTracking modules into the internal expressions buffer.
        /// </summary>
        /// <remarks>
        /// Runs the mutation chain and parameter processing inline on the caller (the
        /// dedicated send loop). This used to bounce through Task.Run for work that takes
        /// microseconds, which cost a threadpool hop every 10ms tick and - worse - made
        /// tracking output hostage to threadpool availability: anything blocking pool
        /// threads elsewhere in the app showed up as avatar stutter. The signature stays
        /// Task-returning for compatibility with existing callers.
        /// </remarks>
        public static Task UpdateData(CancellationToken ct)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var mutated = Mutator.MutateData(Data);
            var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            OnUnifiedDataUpdated.Invoke(mutated);
            var t2 = System.Diagnostics.Stopwatch.GetTimestamp();

            Core.Services.PipelineWatch.LastMutateMs = (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Core.Services.PipelineWatch.LastParamsMs = (t2 - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return Task.CompletedTask;
        }
    }
}