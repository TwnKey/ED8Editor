namespace ED8Editor.Viewer;

internal static class CameraEasing
{
    /// <summary>Applique la courbe d'interpolation du moteur Falcom.</summary>
    /// <param name="t">Temps normalisé 0..1</param>
    /// <param name="type">Valeur du flag interpolation (0=linear, 1=quad-in, 2=quad-out, 3=cos-smooth, 4=sine, -1=cut)</param>
    public static float Apply(float t, int type)
    {
        return type switch
        {
            1 => t * t,                                          // Quadratic Ease-In
            2 => 1f - (1f - t) * (1f - t),                       // Quadratic Ease-Out
            3 => (1f - MathF.Cos(t * MathF.PI)) * 0.5f,          // Cosine Ease-In-Out (Smooth)
            4 => t < 0.5f                                        // Sine Ease-In-Out
                ? MathF.Sin(t * MathF.PI) * 0.5f
                : 1f - MathF.Sin(t * MathF.PI) * 0.5f,
            -1 => t < 1f ? 0f : 1f,                              // Cut / Step
            _ => t,                                               // Linear (default)
        };
    }
}
