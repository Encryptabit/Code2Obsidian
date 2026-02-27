
import sys

with open("Program.cs", "r", encoding="utf-8-sig") as f:
    content = f.read()

# Verify CreateProgress exists before starting
if "CreateProgress" not in content:
    print("CreateProgress already removed or not found")
    sys.exit(0)

changes = 0

# 1. RunWithProgress: change signature and body
old_sig = "Func<ProgressContext, Task<PipelineResult>> operation"
new_sig = "Func<IProgress<PipelineProgress>, Task<PipelineResult>> operation"
if old_sig in content:
    content = content.replace(old_sig, new_sig, 1)
    changes += 1
    print("  [OK] RunWithProgress signature updated")

# Replace the Progress() body with Live() body
old_body = """        PipelineResult result = null!;

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(),
                new ElapsedTimeColumn())
            .StartAsync(async ctx =>
            {
                result = await operation(ctx);
            });

        return result;"""

new_body = """        PipelineResult result = null!;
        var renderLock = new object();

        await AnsiConsole.Live(new Text("Starting..."))
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                var state = new ProgressState();
                var progress = new Progress<PipelineProgress>(p =>
                {
                    lock (renderLock)
                    {
                        state.Update(p);
                        ctx.UpdateTarget(RenderProgressDisplay(state));
                    }
                });
                result = await operation(progress);
            });

        return result;"""

if old_body in content:
    content = content.replace(old_body, new_body, 1)
    changes += 1
    print("  [OK] RunWithProgress body replaced with Live")

# Update doc comment
content = content.replace(
    "Runs an async operation within a Spectre.Console progress context.",
    "Runs an async operation within a Live display with fixed-width character bars.", 1)
content = content.replace(
    "Used for incremental pipeline operations that manage their own progress reporting.",
    "The IProgress callback uses a lock to serialize concurrent updates from parallel enrichment.", 1)

print("  [OK] RunWithProgress docs updated")

# 2. RunFullPipeline: Replace Progress() with Live via RunWithProgress
# The key change: replace the direct Progress().StartAsync with calling RunWithProgress
old_full = """        PipelineResult result = null!;

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(),
                new ElapsedTimeColumn())
            .StartAsync(async ctx =>
            {
                var progress = CreateProgress(ctx);

                // Rebuild enrichers with live progress
                var (liveEnrichers, liveLlm) = BuildEnrichersWithProgress(enrichers, progress);

                var pipeline = new Pipeline.Pipeline(analyzers, liveEnrichers, emitter);
                result = await pipeline.RunAsync(context, outputDir, progress, ct);

                // Copy enrichment metrics from the live enricher
                if (liveLlm is not null)
                {
                    result.EntitiesEnriched = liveLlm.EntitiesEnriched;
                    result.EntitiesCached = liveLlm.EntitiesCached;
                    result.EntitiesFailed = liveLlm.EntitiesFailed;
                    result.InputTokensUsed = liveLlm.InputTokensUsed;
                    result.OutputTokensUsed = liveLlm.OutputTokensUsed;
                }

                // Mark all tasks complete (progress tasks handled by CreateProgress)
            });

        return result;"""

new_full = """        LlmEnricher? liveLlm = null;
        var result = await RunWithProgress(async progress =>
        {
            // Rebuild enrichers with live progress
            var (liveEnrichers, llm) = BuildEnrichersWithProgress(enrichers, progress);
            liveLlm = llm;

            var pipeline = new Pipeline.Pipeline(analyzers, liveEnrichers, emitter);
            return await pipeline.RunAsync(context, outputDir, progress, ct);
        });

        CopyEnrichmentMetrics(result, liveLlm);
        return result;"""

if old_full in content:
    content = content.replace(old_full, new_full, 1)
    changes += 1
    print("  [OK] RunFullPipeline replaced")

content = content.replace(
    "Runs the full pipeline (non-incremental Case A) with Spectre.Console progress.",
    "Runs the full pipeline (non-incremental Case A) with Live display.", 1)

# 3. Update 4 call sites: ctx => { var progress = CreateProgress(ctx); ... } -> async progress => { ... }
# All sites follow the same pattern: replace "ctx =>" with "async progress =>" and remove CreateProgress line

# Replace all "RunWithProgress(ctx =>" with "RunWithProgress(async progress =>"
content = content.replace("RunWithProgress(ctx =>", "RunWithProgress(async progress =>")

# Remove all "var progress = CreateProgress(ctx);" lines (with various indentations)
import re
content = re.sub(r"\n[ \t]*var progress = CreateProgress\(ctx\);\n", "\n", content)

# Change "return incPipeline.Run" to "return await incPipeline.Run" where needed
# (only for lines that don't already have await)
content = re.sub(
    r"return (incPipeline\.Run(?:FullWithStateSaveAsync|DryRunAsync|IncrementalAsync))",
    r"return await \1",
    content
)

changes += 1
print("  [OK] Call sites updated")

# 4. Delete CreateProgress method
start_marker = "    /// <summary>\n    /// Creates a pipeline progress reporter from a Spectre.Console progress context."
end_marker = "    }\n\n    #region Progress Display"

idx_start = content.find(start_marker)
idx_end = content.find(end_marker)

if idx_start >= 0 and idx_end >= 0:
    # Delete from start_marker to just before #region
    # Find the end of the CreateProgress method (closing brace before #region)
    content = content[:idx_start] + content[idx_end:]
    # Clean up: remove trailing "    }\n\n" that was part of the marker
    content = content.replace("    }\n\n    #region Progress Display", "    #region Progress Display")
    changes += 1
    print("  [OK] CreateProgress deleted")
else:
    # Try alternative: just find and remove the entire CreateProgress method
    cp_start = content.find("private static IProgress<PipelineProgress> CreateProgress(ProgressContext ctx)")
    if cp_start >= 0:
        # Find the doc comment start before it
        doc_start = content.rfind("    /// <summary>", 0, cp_start)
        # Find the closing brace
        brace_count = 0
        in_method = False
        method_end = cp_start
        for i in range(cp_start, len(content)):
            if content[i] == "{":
                brace_count += 1
                in_method = True
            elif content[i] == "}":
                brace_count -= 1
                if in_method and brace_count == 0:
                    method_end = i + 1
                    break
        if doc_start >= 0:
            content = content[:doc_start] + content[method_end:]
            changes += 1
            print("  [OK] CreateProgress deleted (alt)")

with open("Program.cs", "w", encoding="utf-8-sig") as f:
    f.write(content)

print(f"\nAll changes applied ({changes} groups)")
