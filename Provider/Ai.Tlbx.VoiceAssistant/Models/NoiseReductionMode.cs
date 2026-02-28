namespace Ai.Tlbx.VoiceAssistant.Models
{
    public enum NoiseReductionMode
    {
        NearField,
        FarField
    }

    public static class NoiseReductionModeExtensions
    {
        public static string ToApiString(this NoiseReductionMode mode) => mode switch
        {
            NoiseReductionMode.NearField => "near_field",
            NoiseReductionMode.FarField => "far_field",
            _ => "far_field"
        };
    }
}
