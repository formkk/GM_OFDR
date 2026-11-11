using System;
using System.Collections.Generic;

namespace Gmix.Data
{
    // ---------------- scan.json ----------------
    public class DeviceInfo
    {
        public string name { get; set; }
        public string model { get; set; }
        public string serial { get; set; }
        public string firmware { get; set; }
        public string sdkVersion { get; set; }
        public string hostIp { get; set; }
        public string deviceIp { get; set; }
    }

    public class ChannelMapInfo
    {
        public int p { get; set; }
        public int s { get; set; }
        public int aux { get; set; }
        public int[] ignored { get; set; }
    }

    public class AcquisitionInfo
    {
        public long sampleRateHz { get; set; }
        public int sampleRateMhz { get; set; }
        public int acqTimeMs { get; set; }
        public int segCount { get; set; }
        public int triggerMode { get; set; }
        public int triggerDelayMs { get; set; }
        public ChannelMapInfo channelMap { get; set; }
        public long expectedFrames { get; set; }
        public long bufferBytes { get; set; }
        public int bytesPerFrame { get; set; }
        public double? sweepPeriodMs { get; set; }
        public double? windowStartMs { get; set; }
        public double? windowEndMs { get; set; }
    }

    public class ConfigSnapshotInfo
    {
        public object @interface { get; set; }
        public object afe { get; set; }
        public object range { get; set; }
        public object functional { get; set; }
        public string hash { get; set; }
    }

    public class DataFileInfo
    {
        public string rawFile { get; set; }
        public string dtype { get; set; }
        public string byteOrder { get; set; }
        public int interleavedChannels { get; set; }
    }

    public class SaturationInfo
    {
        public long ch0 { get; set; }
        public long ch1 { get; set; }
        public long ch2 { get; set; }
        public long ch3 { get; set; }
    }

    public class ZeroCountsInfo
    {
        public long ch0 { get; set; }
        public long ch1 { get; set; }
        public long ch2 { get; set; }
        public long ch3 { get; set; }
    }

    public class ChannelStatInfo
    {
        public int ch { get; set; }
        public int min { get; set; }
        public int max { get; set; }
        public double mean { get; set; }
        public double std { get; set; }
        public double rms { get; set; }
    }

    public class QualityInfo
    {
        public bool shortRead { get; set; }
        public bool frameAligned { get; set; }
        public SaturationInfo saturation { get; set; }
        public ZeroCountsInfo zeroCounts { get; set; }
        public ChannelStatInfo[] channelStats { get; set; }
    }

    public class TimingInfo
    {
        public int? openMs { get; set; }
        public int? readMs { get; set; }
        public int? totalMs { get; set; }
    }

    public class CalibrationRefInfo
    {
        public double distScale { get; set; }
        public string state { get; set; }
        public string calibrationId { get; set; }
    }

    public class ChecksumsInfo
    {
        public string rawSha256 { get; set; }
    }

    public class AnalysisRangeInfo
    {
        public double startM { get; set; }
        public double endM { get; set; }
        public double guardM { get; set; }
    }

    public class SessionInfo
    {
        public int? averageCount { get; set; }
        public string runMode { get; set; }
        public string repeatGroupId { get; set; }
        public AnalysisRangeInfo analysisRange { get; set; }
        public string rlMode { get; set; }
        public bool? zeroPeakExcluded { get; set; }
    }

    public class ScanDocument
    {
        public string schemaVersion { get; set; }
        public string scanId { get; set; }
        public int sequence { get; set; }
        public string capturedAtUtc { get; set; }
        public string dataOrigin { get; set; }
        public string appVersion { get; set; }
        public DeviceInfo device { get; set; }
        public AcquisitionInfo acquisition { get; set; }
        public ConfigSnapshotInfo configSnapshot { get; set; }
        public DataFileInfo data { get; set; }
        public QualityInfo quality { get; set; }
        public TimingInfo timing { get; set; }
        public CalibrationRefInfo calibration { get; set; }
        public string @operator { get; set; }
        public string station { get; set; }
        public string notes { get; set; }
        public ChecksumsInfo checksums { get; set; }
        public SessionInfo session { get; set; }
        public string calibrationId { get; set; }
        public string calibrationState { get; set; }
    }

    // ---------------- ofdr.json ----------------
    public class ProcessorInfo
    {
        public string name { get; set; }
        public string version { get; set; }
        public string sha256 { get; set; }
    }

    public class OfdrConfigInfo
    {
        public double fsHz { get; set; }
        public double distScale { get; set; }
        public long inputLength { get; set; }
        public long outputLength { get; set; }
    }

    public class ArrayInfo
    {
        public string file { get; set; }
        public string dtype { get; set; }
        public long count { get; set; }
        public string unit { get; set; }
    }

    public class OfdrArraysInfo
    {
        public ArrayInfo dist { get; set; }
        public ArrayInfo freq { get; set; }
        public ArrayInfo ps { get; set; }
    }

    public class OfdrQualityInfo
    {
        public bool distMonotonic { get; set; }
        public double distStepM { get; set; }
        public double freqLastHz { get; set; }
        public bool psPositive { get; set; }
    }

    public class OfdrDocument
    {
        public string schemaVersion { get; set; }
        public string scanId { get; set; }
        public ProcessorInfo processor { get; set; }
        public OfdrConfigInfo config { get; set; }
        public OfdrArraysInfo arrays { get; set; }
        public OfdrQualityInfo quality { get; set; }
    }

    // ---------------- events.json ----------------
    public class DetectorInfo
    {
        public string name { get; set; }
        public string version { get; set; }
        public string sha256 { get; set; }
    }

    public class DetectConfigInfo
    {
        public double nfQ { get; set; }
        public double nfRadiusM { get; set; }
        public long pkBaselineWin { get; set; }
        public double pkMinProminenceDb { get; set; }
        public double pkMinDistanceM { get; set; }
        public double pkExcludeRadiusM { get; set; }
        public double pkSegPercentile { get; set; }
        public double pkLastSegTailM { get; set; }
    }

    public class EventCountsInfo
    {
        public int peaks { get; set; }
        public int segments { get; set; }
        public int events { get; set; }
    }

    public class PeakInfo
    {
        public double locM { get; set; }
        public double heightDbRel { get; set; }
        public double baseDbRel { get; set; }
        public double prominenceDb { get; set; }
        public int? peakNo { get; set; }
        public double? distanceM { get; set; }
        public bool? zeroPeak { get; set; }
    }

    public class SegmentInfo
    {
        public long startIndex { get; set; }
        public long endIndex { get; set; }
        public double noiseFloorDbRel { get; set; }
    }

    public class EventInfo
    {
        public double locM { get; set; }
        public double peakDbRel { get; set; }
        public double? ilDb { get; set; }
        public double? rlDb { get; set; }
        public double? rlDbRel { get; set; }
        public double? baseBeforeDbRel { get; set; }
        public double? baseAfterDbRel { get; set; }
        public double? stepLossDb { get; set; }
        public bool? zeroPeak { get; set; }
    }

    public class UnitsInfo
    {
        public string level { get; set; }
        public string distance { get; set; }
    }

    public class DisplayInfo
    {
        public int peakCount { get; set; }
        public bool zeroPeakExcluded { get; set; }
    }

    public class EventsArraysInfo
    {
        public ArrayInfo baseline { get; set; }
    }

    public class EventsDocument
    {
        public string schemaVersion { get; set; }
        public string scanId { get; set; }
        public DetectorInfo detector { get; set; }
        public DetectConfigInfo config { get; set; }
        public EventCountsInfo counts { get; set; }
        public PeakInfo[] peaks { get; set; }
        public SegmentInfo[] segments { get; set; }
        public EventInfo[] events { get; set; }
        public bool zeroPeakIncluded { get; set; }
        public UnitsInfo units { get; set; }
        public DisplayInfo display { get; set; }
        public EventsArraysInfo arrays { get; set; }
    }

    // ---------------- summary.json ----------------
    public class SummaryDocument
    {
        public string schemaVersion { get; set; }
        public string scanId { get; set; }
        public AnalysisRangeInfo analysisRange { get; set; }
        public string rlMode { get; set; }
        public int averageCount { get; set; }
        public double? totalIlRawDb { get; set; }
        public double? totalIlDb { get; set; }
        public double? totalRlRawDb { get; set; }
        public double? totalRlDb { get; set; }
        public double? ilPrecisionDb { get; set; }
        public double? rlPrecisionDb { get; set; }
        public int peakCount { get; set; }
        public int eventCount { get; set; }
        public string[] warnings { get; set; }
    }

    // ---------------- calibration.json ----------------
    public class ReferenceInfo
    {
        public string type { get; set; }
        public double? value { get; set; }
        public string serial { get; set; }
    }

    public class ValidationPoint
    {
        public double referenceValue { get; set; }
        public double measuredValue { get; set; }
        public double residualDb { get; set; }
    }

    public class CalibrationDocument
    {
        public string schemaVersion { get; set; }
        public string calibrationId { get; set; }
        public string createdAtUtc { get; set; }
        public string @operator { get; set; }
        public string configHash { get; set; }
        public string method { get; set; }
        public double distScale { get; set; }
        public double? rlOffsetK { get; set; }
        public string rlCurveKFile { get; set; }
        public double? ilSlope { get; set; }
        public double? ilIntercept { get; set; }
        public double? triggerThresholdDb { get; set; }
        public ReferenceInfo reference { get; set; }
        public ValidationPoint[] validationPoints { get; set; }
    }

    // ---------------- view.json ----------------
    public class CursorInfo
    {
        public double x { get; set; }
        public double y { get; set; }
    }

    public class ViewDocument
    {
        public string schemaVersion { get; set; }
        public string scanId { get; set; }
        public double[] xRange { get; set; }
        public double[] yRange { get; set; }
        public CursorInfo[] cursor { get; set; }
        public string activeTab { get; set; }
        public int? selectedPeakNo { get; set; }
    }
}
