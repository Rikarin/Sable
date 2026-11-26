using System.Globalization;
using System.Text;

namespace Sable.SIL;

/// <summary>
///     Profile-guided speculative devirtualisation (#179, docs/05 §5.1): what
///     a program's calls through a vtable, a witness table or a closure
///     reached as it ran, counted for each call site, and the most reached
///     tested for and called directly where the next build is given that.
/// </summary>
/// <remarks>
///     <para>
///         <c>sable build --profile-generate</c> counts the callee of every
///         such call (<see cref="InstrumentProfile" />): the runtime keeps, for
///         each site, the first four functions it reached and how often, and
///         how often anything else, and writes them as the program ends
///         (runtime/profile.c). <c>--profile-use</c> reads that
///         (<see cref="ProfileData" />), and a site whose most reached function
///         had at least <see cref="Dominant" /> of its calls, and at least
///         <see cref="Fewest" />, tests for that function and calls it directly
///         (<see cref="UseProfile" />) — where the inliner can then take its
///         body — the call as it was left for anything else. The test is of
///         the function itself, so a wrong guess costs a comparison, never a
///         wrong call.
///     </para>
///     <para>
///         A site is named by its function, which of the function's indirect
///         calls it is, and a fingerprint of the function's SIL as it is when
///         counted: so a function changed since, or built another way, has
///         sites no profile names, and a stale profile is only unused. Both
///         passes run at the same place among the optimising passes, after
///         the static devirtualisation and before the inliner.
///     </para>
/// </remarks>
public static class CallProfile {
    /// <summary>The first line of a profile.</summary>
    public const string Header = "sable-profile 1";

    /// <summary>The share of a site's calls its most reached function must have had to be guessed.</summary>
    public const double Dominant = 0.9;

    /// <summary>The fewest calls a site must have made to be guessed for.</summary>
    public const long Fewest = 64;

    /// <summary>A call through a function value: a vtable's, a witness table's, or a closure.</summary>
    internal static bool Indirect(SilInstruction call) =>
        call is ApplyInst or TryApplyInst
        && call.Operands[0] switch {
            ClassMethodInst { Type: FunctionType { IsAsync: false } } => true,
            BuiltinInst { Type: FunctionType { IsAsync: false } } witness => witness.Name.StartsWith("witness_generic:", StringComparison.Ordinal),
            var closure => closure.Type is ThickFunctionType { Signature.IsAsync: false },
        };

    /// <summary>Each indirect call of the module, with its key: the function, the call's place among the function's, and the function's fingerprint.</summary>
    internal static List<(SilFunction Function, SilInstruction Call, string Key)> Sites(SilModule module) {
        var sites = new List<(SilFunction, SilInstruction, string)>();
        foreach (var function in module.Functions) {
            if (function.Blocks.Count == 0 || function.Type.IsAsync) {
                continue;
            }

            string? fingerprint = null;
            var ordinal = 0;
            foreach (var call in function.Blocks.SelectMany(static b => b.Instructions).Where(Indirect)) {
                fingerprint ??= Fingerprint(function);
                sites.Add((function, call, $"{function.Name}#{ordinal++}@{fingerprint}"));
            }
        }

        return sites;
    }

    /// <summary>The 64-bit FNV-1a hash of the function's SIL, in hex.</summary>
    static string Fingerprint(SilFunction function) {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(SilPrinter.Print(function))) {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}

/// <summary>
///     What a profile says of one call site: how often it was made, and how
///     often each function it reached, of those the runtime kept.
/// </summary>
public sealed record ProfileSite(long Total, IReadOnlyList<(string Function, long Count)> Targets);

/// <summary>A profile, read (<see cref="CallProfile" />): its sites by key.</summary>
public sealed class ProfileData(IReadOnlyDictionary<string, ProfileSite> sites) {
    public IReadOnlyDictionary<string, ProfileSite> Sites { get; } = sites;

    /// <summary>
    ///     Reads a profile's text: its header, then for each site a line
    ///     <c>site, key, total</c> and one <c>target, count, function</c> for
    ///     each function, fields separated by tabs. Null, and why, for text
    ///     that is not one.
    /// </summary>
    public static ProfileData? Parse(string text, out string? problem) {
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != CallProfile.Header) {
            problem = $"it does not start with '{CallProfile.Header}'";
            return null;
        }

        var sites = new Dictionary<string, ProfileSite>(StringComparer.Ordinal);
        string? key = null;
        long total = 0;
        var targets = new List<(string, long)>();

        void Finish() {
            if (key is not null) {
                sites[key] = new ProfileSite(total, [..targets]);
            }

            targets.Clear();
        }

        for (var i = 1; i < lines.Length; i++) {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) {
                continue;
            }

            var fields = line.Split('\t');
            switch (fields) {
                case ["site", var named, var count] when long.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var calls):
                    Finish();
                    key = named;
                    total = calls;
                    break;
                case ["target", var count, var function] when key is not null && long.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var calls):
                    targets.Add((function, calls));
                    break;
                default:
                    problem = $"line {i + 1} is not a site or a target";
                    return null;
            }
        }

        Finish();
        problem = null;
        return new ProfileData(sites);
    }
}

/// <summary>
///     Counts the callee of each indirect call (<see cref="CallProfile" />): a
///     <c>profile_count</c> of the site's number and the function or closure
///     called, before the call; the sites' keys go in the module, for the
///     code generator to give the runtime.
/// </summary>
public sealed class InstrumentProfile(string file) : ISilPass {
    public string Name => "instrument-profile";

    public void Run(SilFunction function) { }

    public void Run(SilModule module) {
        var sites = CallProfile.Sites(module);
        module.ProfileSites = [..sites.Select(static s => s.Key)];
        module.ProfileFile = file;
        for (var i = 0; i < sites.Count; i++) {
            var (_, call, _) = sites[i];
            var block = call.Block;
            var at = block.Instructions.IndexOf(call);
            var number = new IntegerLiteralInst(SilType.Int, i) { Block = block, Location = call.Location };
            var count = new BuiltinInst("profile_count", SilType.Unit, number, call.Operands[0]) { Block = block, Location = call.Location };
            block.Instructions.Insert(at, count);
            block.Instructions.Insert(at, number);
        }
    }
}

/// <summary>
///     Guesses each indirect call's callee where a profile says one function
///     took nearly all its calls (<see cref="CallProfile" />): <c>function_is</c>
///     tests for it, and it is called directly — a closure's code given the
///     closure's context — the call as it was left for anything else.
/// </summary>
public sealed class UseProfile(ProfileData profile) : ISilPass {
    public string Name => "use-profile";

    /// <summary>How many sites the profile's guesses were made at, in the last module the pass ran over.</summary>
    public int Guessed { get; private set; }

    public void Run(SilFunction function) { }

    public void Run(SilModule module) {
        Guessed = 0;
        foreach (var (function, call, key) in CallProfile.Sites(module)) {
            if (!profile.Sites.TryGetValue(key, out var site) || site.Total < CallProfile.Fewest || site.Targets.Count == 0) {
                continue;
            }

            var (name, count) = site.Targets.MaxBy(static t => t.Count);
            if (count < CallProfile.Dominant * site.Total || module.Find(name) is not { Blocks.Count: > 0 } target || !Fits(call, target)) {
                continue;
            }

            Speculate(function, call, target);
            Guessed++;
        }
    }

    /// <summary>Whether the function can be called where the call's callee is: the same signature, a method's self a subclass of the function's; a closure's code taking its context last.</summary>
    static bool Fits(SilInstruction call, SilFunction target) {
        var type = target.Type;
        switch (call.Operands[0].Type) {
            case ThickFunctionType closure: {
                var code = closure.Implementation;
                return type.Parameters.SequenceEqual(code.Parameters) && type.Result == code.Result && type.Thrown == code.Thrown
                    && type.Owned.SequenceEqual(code.Owned) && !type.IsAsync;
            }

            case FunctionType called:
                return type.Parameters.Length == called.Parameters.Length && type.Result == called.Result && type.Thrown == called.Thrown
                    && type.Owned.SequenceEqual(called.Owned) && !type.IsAsync
                    && type.Parameters.Skip(1).SequenceEqual(called.Parameters.Skip(1))
                    && (type.Parameters.Length == 0 || type.Parameters[0] == called.Parameters[0]
                        || call.Operands[0] is ClassMethodInst && !type.IsOwned(0)
                        && called.Parameters[0] is ClassType self && type.Parameters[0] is ClassType own && (own.IsSubclassOf(self) || self.IsSubclassOf(own)));

            default:
                return false;
        }
    }

    /// <summary>
    ///     The call made two: a test of its callee against the function, and
    ///     a direct call of that where it holds; the call itself otherwise.
    ///     The two meet where the call went on, with its result.
    /// </summary>
    static void Speculate(SilFunction function, SilInstruction call, SilFunction target) {
        var block = call.Block;
        var location = call.Location;
        var at = block.Instructions.IndexOf(call);
        var callee = call.Operands[0];
        var arguments = call.Operands.Skip(1).ToList();

        // An apply's block is split after it; a try_apply ends its block.
        SilBlock? rest = null;
        if (call is ApplyInst) {
            rest = function.AddBlock();
            foreach (var moved in block.Instructions.Skip(at + 1)) {
                moved.Block = rest;
                rest.Instructions.Add(moved);
            }

            block.Instructions.RemoveRange(at + 1, block.Instructions.Count - at - 1);
            if (call.HasResult) {
                var result = rest.AddArgument(call.Type);
                Uses.Replace(function, call, result);
            }
        }

        block.Instructions.RemoveAt(at);
        var reference = block.Append(new FunctionRefInst(target.Name, target.Type) { Location = location });
        var guessed = block.Append(new BuiltinInst("function_is", SilType.Bool, callee, reference) { Location = location });
        var direct = function.AddBlock();
        var fallback = function.AddBlock();
        block.Append(new CondBranchInst(guessed, direct, [], fallback, []) { Location = location });

        // The guess: a method's self seen as the class the function takes it
        // as (its class inherits that function, so it is one); a closure's
        // context copied for its code, and destroyed after.
        SilValue? context = null;
        if (callee.Type is ThickFunctionType) {
            context = direct.Append(new BuiltinInst("closure_context", SilType.Object, callee) { Location = location });
            arguments.Add(context);
        } else if (arguments.Count > 0 && target.Type.Parameters[0] != arguments[0].Type) {
            arguments[0] = direct.Append(new UncheckedRefCastInst(arguments[0], (ClassType)target.Type.Parameters[0]) { Location = location });
        }

        switch (call) {
            case ApplyInst apply: {
                var made = direct.Append(new ApplyInst(reference, apply.Type, [..arguments]) { Location = location });
                if (context is not null) {
                    direct.Append(new DestroyValueInst(context) { Location = location });
                }

                direct.Append(new BranchInst(rest!, apply.HasResult ? [made] : []) { Location = location });
                fallback.Append(apply);
                fallback.Append(new BranchInst(rest!, apply.HasResult ? [apply] : []) { Location = location });
                break;
            }

            case TryApplyInst tryApply: {
                var normal = Through(function, tryApply.Normal, context, location);
                var error = Through(function, tryApply.Error, context, location);
                direct.Append(new TryApplyInst(reference, tryApply.ResultType, [..arguments], normal, error) { Location = location });
                fallback.Append(tryApply);
                break;
            }
        }
    }

    /// <summary>A block that passes what it is given on to <paramref name="to" />, the context destroyed first.</summary>
    static SilBlock Through(SilFunction function, SilBlock to, SilValue? context, SilLocation? location) {
        var through = function.AddBlock();
        var passed = to.Arguments.Select(a => (SilValue)through.AddArgument(a.Type, a.IsGuaranteed)).ToArray();
        if (context is not null) {
            through.Append(new DestroyValueInst(context) { Location = location });
        }

        through.Append(new BranchInst(to, passed) { Location = location });
        return through;
    }
}
