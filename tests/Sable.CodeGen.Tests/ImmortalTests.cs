using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>@immortal static let</c> (docs/02 §2.2): the objects the global
///     holds are never counted or released — their deinits never run, where
///     a mortal global's run as the program ends — and they, and what they
///     keep alive, are no leak. Native and interpreted, leak-checked.
/// </summary>
[Native]
public sealed class ImmortalTests {
    const string Declarations = """
                                class Node {
                                    var name: String
                                    var next: Node?
                                    init(_ name: String, _ next: Node?) {
                                        self.name = name
                                        self.next = next
                                    }
                                    deinit { print("bye \(name)") }
                                }

                                class Registry {
                                    var items: Node[>..] = []
                                    var tag: String
                                    init(_ tag: String) { self.tag = tag }
                                    fn add(_ name: String) { items.append(Node(name, none)) }
                                    deinit { print("registry \(tag) gone") }

                                    @immortal static let shared = Registry("immortal")
                                    static let mortal = Registry("mortal")
                                }

                                struct Settings {
                                    var title: String
                                    var root: Node
                                    @immortal static let defaults = Settings(title: "t" + "itle", root: Node("root", Node("child", none)))
                                }
                                """;

    [Fact]
    public void AnImmortalGlobalsObjectsAreNeverReleased() =>
        Assert.Equal(
            "3\nc\nbye child\nchild\ntitle\n1\nregistry mortal gone\nbye m\n",
            AssertAgree(
                Program(
                    Declarations,
                    """
                        Registry.shared.add("a")
                        Registry.shared.add("b")
                        let r = Registry.shared
                        r.add("c")
                        print(Registry.shared.items.count)
                        print(Registry.shared.items[2].name)
                        // A node it held, replaced, is released as any object is.
                        Settings.defaults.root.next = Node("child", none)
                        print(Settings.defaults.root.next!.name)
                        print(Settings.defaults.title)
                        Registry.mortal.add("m")
                        print(Registry.mortal.items.count)
                    """
                )
            ).Stdout
        );

    [Fact]
    public void TheirObjectsAreMadeImmortalOnceTheGlobalIsSet() {
        var result = Pipeline.Compile(new SourceText(Program(Declarations, "    print(Registry.shared.items.count)"), "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Contains("builtin \"make_immortal\"", SilPrinter.Print(result.Sil!), StringComparison.Ordinal);
        Assert.Contains("call void @sable_make_immortal(ptr", result.LlvmIr!, StringComparison.Ordinal);
    }
}
