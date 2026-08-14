using System;
using System.IO;
using Microsoft.ML;
using Microsoft.ML.Data;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>
/// Multiclass classifier that diagnoses a PID configuration — given the gains in use
/// and the response they actually produced, predicts a corrective action label
/// (e.g. "SISTEM_SUDAH_IDEAL", "TURUNKAN_KP_DAN_KI"). Trained from
/// DataDiagnosisPID_Dashboard.csv on construction.
/// </summary>
public class PidDiagnosisAgent
{
    private readonly MLContext _mlContext;
    private PredictionEngine<PidDiagnosisTrainingData, PidDiagnosisPrediction>? _predictor;

    // ML.NET's PredictionEngine is explicitly documented as not thread-safe, and the
    // server hands each connection to a fire-and-forget task (ShareService), so two
    // students hitting /sim/pid at once would race on the same engine. A single
    // prediction is microseconds next to the LLM call that follows, so serialising is
    // cheaper than the PredictionEnginePool this would otherwise need.
    private readonly object _predictLock = new();

    private static readonly string DataPath =
        Path.Combine(AppContext.BaseDirectory, "DataDiagnosisPID_Dashboard.csv");

    public PidDiagnosisAgent()
    {
        _mlContext = new MLContext(seed: 1);
        TrainModel();
    }

    private void TrainModel()
    {
        if (!File.Exists(DataPath)) return;

        IDataView dataView = _mlContext.Data.LoadFromTextFile<PidDiagnosisTrainingData>(
            DataPath, hasHeader: true, separatorChar: ',');

        var pipeline = _mlContext.Transforms.Conversion
            .MapValueToKey("Label", nameof(PidDiagnosisTrainingData.Rekomendasi_Aksi))
            .Append(_mlContext.Transforms.Concatenate("Features",
                nameof(PidDiagnosisTrainingData.Kp_Saat_Ini),
                nameof(PidDiagnosisTrainingData.Ki_Saat_Ini),
                nameof(PidDiagnosisTrainingData.Kd_Saat_Ini),
                nameof(PidDiagnosisTrainingData.Overshoot_Riil),
                nameof(PidDiagnosisTrainingData.RiseTime_Riil),
                nameof(PidDiagnosisTrainingData.SettlingTime_Riil),
                nameof(PidDiagnosisTrainingData.SteadyStateError),
                nameof(PidDiagnosisTrainingData.Is_Stable)))
            .Append(_mlContext.MulticlassClassification.Trainers.SdcaMaximumEntropy())
            .Append(_mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

        var model = pipeline.Fit(dataView);
        _predictor = _mlContext.Model.CreatePredictionEngine<PidDiagnosisTrainingData, PidDiagnosisPrediction>(model);
    }

    public PidDiagnosisResult Predict(PidDiagnosisInput input)
    {
        if (_predictor is null)
            return new PidDiagnosisResult { Rekomendasi_Aksi = "TIDAK_DIKETAHUI" };

        var row = new PidDiagnosisTrainingData
        {
            Kp_Saat_Ini        = input.Kp_Saat_Ini,
            Ki_Saat_Ini        = input.Ki_Saat_Ini,
            Kd_Saat_Ini        = input.Kd_Saat_Ini,
            Overshoot_Riil     = input.Overshoot_Riil,
            RiseTime_Riil      = input.RiseTime_Riil,
            SettlingTime_Riil  = input.SettlingTime_Riil,
            SteadyStateError   = input.SteadyStateError,
            Is_Stable          = input.Is_Stable,
            // Rekomendasi_Aksi left blank — it's the training label, unused at prediction time.
        };

        PidDiagnosisPrediction pred;
        lock (_predictLock) pred = _predictor.Predict(row);
        return new PidDiagnosisResult { Rekomendasi_Aksi = pred.PredictedLabel };
    }

    public class PidDiagnosisTrainingData
    {
        [LoadColumn(0)] public float Kp_Saat_Ini { get; set; }
        [LoadColumn(1)] public float Ki_Saat_Ini { get; set; }
        [LoadColumn(2)] public float Kd_Saat_Ini { get; set; }
        [LoadColumn(3)] public float Overshoot_Riil { get; set; }
        [LoadColumn(4)] public float RiseTime_Riil { get; set; }
        [LoadColumn(5)] public float SettlingTime_Riil { get; set; }
        [LoadColumn(6)] public float SteadyStateError { get; set; }
        [LoadColumn(7)] public float Is_Stable { get; set; }
        [LoadColumn(8)] public string Rekomendasi_Aksi { get; set; } = "";
    }

    public class PidDiagnosisPrediction
    {
        public string PredictedLabel { get; set; } = "";
    }
}
