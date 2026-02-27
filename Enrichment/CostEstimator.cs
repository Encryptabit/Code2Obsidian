namespace Code2Obsidian.Enrichment;

/// <summary>
/// Provides token estimation, cost calculation, and confirmation threshold checking.
/// Uses the chars/4 heuristic for token estimation per research findings.
/// </summary>
public static class CostEstimator
{
    /// <summary>
    /// Default threshold of uncached entities above which the user should be prompted to confirm.
    /// Below this count, enrichment auto-proceeds.
    /// </summary>
    private const int ConfirmationThreshold = 50;

    /// <summary>
    /// Estimates token count for a text string using the ~1 token per 4 characters heuristic.
    /// </summary>
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return (int)Math.Ceiling(text.Length / 4.0);
    }

    /// <summary>
    /// Estimates total input/output tokens and cost for a batch of uncached entities.
    /// </summary>
    /// <param name="uncachedCount">Number of entities requiring LLM calls.</param>
    /// <param name="avgInputTokensPerPrompt">Average input tokens per prompt (from sampling).</param>
    /// <param name="maxOutputTokens">Maximum output tokens per response (from config).</param>
    /// <param name="costPerInputToken">Cost per input token (from config).</param>
    /// <param name="costPerOutputToken">Cost per output token (from config).</param>
    /// <returns>Tuple of total input tokens, total output tokens, and estimated cost.</returns>
    public static (int totalInputTokens, int totalOutputTokens, decimal estimatedCost) EstimateCost(
        int uncachedCount,
        int avgInputTokensPerPrompt,
        int maxOutputTokens,
        decimal costPerInputToken,
        decimal costPerOutputToken)
    {
        var totalInputTokens = uncachedCount * avgInputTokensPerPrompt;
        var totalOutputTokens = uncachedCount * maxOutputTokens;
        var estimatedCost = totalInputTokens * costPerInputToken + totalOutputTokens * costPerOutputToken;

        return (totalInputTokens, totalOutputTokens, estimatedCost);
    }

    /// <summary>
    /// Returns true when the number of uncached entities is large enough to warrant
    /// user confirmation before proceeding with LLM calls.
    /// Threshold: 50 entities. Below 50 auto-proceeds; at or above 50, prompts.
    /// </summary>
    public static bool ShouldConfirm(int uncachedCount)
    {
        return uncachedCount >= ConfirmationThreshold;
    }
}
