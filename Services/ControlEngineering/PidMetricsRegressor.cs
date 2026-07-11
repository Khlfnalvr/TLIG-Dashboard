using System;
using System.IO;
using Microsoft.ML;
using Microsoft.ML.Data;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>
/// Multi-output regression predicting step-response metrics (Overshoot, RiseTime,
/// SettlingTime, SteadyStateError) directly from Kp/Ki/Kd — ML.NET has no native
/// joint multi-output trainer, so this trains four independent FastTree regressors
/// (one per target column) that share the same Kp/Ki/Kd feature vector. This is an
/// instant statistical estimate, distinct from <see cref="PidSimulator"/>'s exact
/// RK4 integration, which remains the source of the plotted response curve. Trained
/// from DataDiagnosisPID_Dashboard.csv on construction (same file PidDiagnosisAgent
/// uses — it already carries this exact Kp/Ki/Kd → metrics mapping).
/// </summary>
public class PidMetricsRegressor
{
    private readonly MLContext _mlContext;
    private PredictionEngine<PidMetricsTrainingData, PidMetricsScore>? _predOvershoot;
    private PredictionEngine<PidMetricsTrainingData, PidMetricsScore>? _predRiseTime;
    private PredictionEngine<PidMetricsTrainingData, PidMetricsScore>? _predSettlingTime;
    private PredictionEngine<PidMetricsTrainingData, PidMetricsScore>? _predSteadyStateError;

    private static readonly string DataPath =
        Path.Combine(AppContext.BaseDirectory, "DataDiagnosisPID_Dashboard.csv");

    public PidMetricsRegressor()
    {
        _mlContext = new MLContext(seed: 1);
        TrainModels();
    }

    private void TrainModels()
    {
        if (!File.Exists(DataPath)) return;

        IDataView dataView = _mlContext.Data.LoadFromTextFile<PidMetricsTrainingData>(
            DataPath, hasHeader: true, separatorChar: ',');

        _predOvershoot        = TrainOne(dataView, nameof(PidMetricsTrainingData.Overshoot_Riil));
        _predRiseTime         = TrainOne(dataView, nameof(PidMetricsTrainingData.RiseTime_Riil));
        _predSettlingTime     = TrainOne(dataView, nameof(PidMetricsTrainingData.SettlingTime_Riil));
        _predSteadyStateError = TrainOne(dataView, nameof(PidMetricsTrainingData.SteadyStateError));
    }

    private PredictionEngine<PidMetricsTrainingData, PidMetricsScore> TrainOne(IDataView dataView, string labelColumn)
    {
        var pipeline = _mlContext.Transforms.Concatenate("Features",
                nameof(PidMetricsTrainingData.Kp_Saat_Ini),
                nameof(PidMetricsTrainingData.Ki_Saat_Ini),
                nameof(PidMetricsTrainingData.Kd_Saat_Ini))
            .Append(_mlContext.Regression.Trainers.FastTree(labelColumnName: labelColumn));

        var model = pipeline.Fit(dataView);
        return _mlContext.Model.CreatePredictionEngine<PidMetricsTrainingData, PidMetricsScore>(model);
    }

    public PidMetricsPrediction Predict(float kp, float ki, float kd)
    {
        if (_predOvershoot is null || _predRiseTime is null || _predSettlingTime is null || _predSteadyStateError is null)
            return new PidMetricsPrediction();

        var row = new PidMetricsTrainingData { Kp_Saat_Ini = kp, Ki_Saat_Ini = ki, Kd_Saat_Ini = kd };

        // Regression can extrapolate to physically impossible negative durations for
        // gain combinations sparsely represented in the training data — clamp rather
        // than surface a negative settling/rise time or steady-state error to the UI.
        return new PidMetricsPrediction
        {
            Overshoot        = Math.Max(0, _predOvershoot.Predict(row).Score),
            RiseTime         = Math.Max(0, _predRiseTime.Predict(row).Score),
            SettlingTime     = Math.Max(0, _predSettlingTime.Predict(row).Score),
            SteadyStateError = Math.Max(0, _predSteadyStateError.Predict(row).Score),
        };
    }

    // Same 9-column schema as PidDiagnosisAgent's training row — both agents train
    // off the same CSV. Is_Stable/Rekomendasi_Aksi are loaded but unused here.
    public class PidMetricsTrainingData
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

    public class PidMetricsScore
    {
        [ColumnName("Score")] public float Score { get; set; }
    }
}
