namespace Ai.Tlbx.VoiceAssistant.Provider.XAi.Models
{
    /// <summary>
    /// Built-in assistant voices shared by the xAI Voice Agent and Text to Speech APIs.
    /// Custom voices can be selected through XaiVoiceSettings.VoiceId.
    /// </summary>
    public enum XaiVoice
    {
        Carina,
        Zagan,
        Helix,
        Orion,
        Luna,
        Iris,
        Altair,
        Zenith,
        Perseus,
        Helios,
        Lux,
        Kepler,
        Rigel,
        Cosmo,
        Celeste,
        Ursa,
        Sirius,
        Lumen,
        Castor,
        Naksh,
        Atlas,

        /// <summary>
        /// Ara - Female, warm and friendly. Default voice, balanced and conversational.
        /// </summary>
        Ara,

        /// <summary>
        /// Rex - Male, confident and clear. Professional and articulate, ideal for business applications.
        /// </summary>
        Rex,

        /// <summary>
        /// Sal - Neutral, smooth and balanced. Versatile voice suitable for various contexts.
        /// </summary>
        Sal,

        /// <summary>
        /// Eve - Female, energetic and upbeat. Engaging and enthusiastic, great for interactive experiences.
        /// </summary>
        Eve,

        /// <summary>
        /// Leo - Male, authoritative and strong. Decisive and commanding, suitable for instructional content.
        /// </summary>
        Leo
    }
}
