namespace Aleph;

public interface IAether
{
    IAether.IMathGateway Math { get; }
    IAether.IMlGateway Ml { get; }
    IAether.ISimGateway Sim { get; }
    IAether.IMacroGateway Macro { get; }

    public interface IMathGateway
    {
        Task<AetherJsonResult> EvaluateIndicatorsAsync(MathIndicatorsRequest request, CancellationToken ct = default);
    }

    public interface IMlGateway
    {
        Task<AetherJsonResult> PredictAsync(MlPredictRequest request, CancellationToken ct = default);
        Task<AetherJsonResult> TrainAsync(MlTrainRequest request, CancellationToken ct = default);
        Task<AetherJsonResult> GetStatusAsync(MlStatusRequest request, CancellationToken ct = default);
    }

    public interface ISimGateway
    {
        Task<AetherJsonResult> RunBacktestAsync(SimBacktestRequest request, CancellationToken ct = default);
    }

    public interface IMacroGateway
    {
        Task<AetherJsonResult> CheckRegimeAsync(MacroRegimeRequest request, CancellationToken ct = default);
    }
}

public sealed record AetherJsonResult(
    bool Success,
    string PayloadJson,
    string? Error,
    int ExitCode,
    bool TimedOut);

public sealed record MathIndicatorsRequest(
    string Symbol,
    int Days = 30,
    string Timeframe = "1d");

public sealed record MlPredictRequest(
    string Symbol,
    int HorizonDays = 5);

public sealed record MlTrainRequest(
    string Symbol,
    int Epochs = 1);

public sealed record MlStatusRequest(
    string? Symbol = null);

public sealed record SimBacktestRequest(
    string Symbol,
    int Days = 180,
    string Strategy = "baseline");

public sealed record MacroRegimeRequest(
    string Region = "global");
