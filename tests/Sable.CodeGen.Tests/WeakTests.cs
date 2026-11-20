using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>weak</c> and <c>unowned</c> (docs/02 §2.2): references that do not
///     keep their object alive. A weak one reads as none once its object is
///     gone; an unowned one traps. A cycle with a weak edge is freed whole.
///     Native and interpreted, leak-checked.
/// </summary>
[Native]
public sealed class WeakTests {
    const string Workspace = """
                             class Workspace {
                                 var name: String
                                 var documents: Document[>..]

                                 init(_ name: String) {
                                     self.name = name
                                     self.documents = []
                                 }

                                 fn add(_ title: String) -> Document {
                                     let document = Document(title, self)
                                     documents.append(document)
                                     return document
                                 }

                                 deinit {
                                     print("workspace " + name + " closed")
                                 }
                             }

                             class Document {
                                 var title: String
                                 weak var owner: Workspace?

                                 init(_ title: String, _ owner: Workspace) {
                                     self.title = title
                                     self.owner = owner
                                 }

                                 fn describe() -> String {
                                     if let owner = self.owner {
                                         return title + " in " + owner.name
                                     }
                                     return title + " on its own"
                                 }

                                 deinit {
                                     print("document " + title + " closed")
                                 }
                             }
                             """;

    [Fact]
    public void AWeakBackReferenceBreaksTheCycleAndReadsNoneOnceItsObjectIsGone() =>
        Assert.Equal(
            "notes in home\nplan in home\nworkspace home closed\ndocument plan closed\nnotes on its own\ndocument notes closed\n",
            AssertAgree(
                Program(
                    Workspace + """

                                fn openNotes() -> Document {
                                    let home = Workspace("home")
                                    let notes = home.add("notes")
                                    print(notes.describe())
                                    print(home.add("plan").describe())
                                    return notes
                                }
                                """,
                    """
                        let kept = openNotes()
                        print(kept.describe())
                    """
                )
            ).Stdout
        );

    const string Tree = """
                        class Parent {
                            var name: String
                            init(_ name: String) {
                                self.name = name
                            }
                            deinit {
                                print("parent gone")
                            }
                        }

                        class Child {
                            unowned var parent: Parent
                            init(_ parent: Parent) {
                                self.parent = parent
                            }
                            fn parentName() -> String {
                                return parent.name
                            }
                        }
                        """;

    [Fact]
    public void AnUnownedReferenceReadsItsObjectWhileItLives() =>
        Assert.Equal(
            "root\nparent gone\n",
            AssertAgree(
                Program(
                    Tree,
                    """
                        let parent = Parent("root")
                        let child = Child(parent)
                        print(child.parentName())
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AnUnownedReferenceTrapsOnceItsObjectIsGone() {
        var execution = AssertAgree(
            Program(
                Tree + """

                       fn orphan() -> Child {
                           let parent = Parent("brief")
                           return Child(parent)
                       }
                       """,
                """
                    let child = orphan()
                    print("still here")
                    print(child.parentName())
                """
            )
        );
        Assert.Equal("parent gone\nstill here\n", execution.Stdout);
        Assert.Equal("fatal error: read an unowned reference to an object that is gone\n  at test.sb:18:9\n", execution.Stderr);
    }

    /// <summary>
    ///     A class made by its implicit 'init()' gives a weak field its initial
    ///     value as a weak store: it failed SIL verification as a plain store.
    /// </summary>
    [Fact]
    public void AWeakFieldWithAnInitialValueInAClassMadeByInit() =>
        Assert.Equal(
            "true\nfalse\n",
            AssertAgree(
                Program(
                    """
                    class Owner {
                        var pet: Pet? = none
                    }

                    class Pet {
                        weak var owner: Owner? = none
                    }
                    """,
                    """
                        let pet = Pet()
                        print(pet.owner == none)
                        let owner = Owner()
                        owner.pet = pet
                        pet.owner = owner
                        print(pet.owner == none)
                    """
                )
            ).Stdout
        );

    const string Links = """
                         class Node {
                             var name: String
                             init(_ name: String) {
                                 self.name = name
                             }
                             deinit {
                                 print("bye " + name)
                             }
                         }

                         struct Link {
                             weak var target: Node?
                             unowned var owner: Node
                         }

                         struct Tagged {
                             weak var node: Node?
                             var tag: Int
                             init(_ node: Node?, _ tag: Int) {
                                 self.node = node
                                 self.tag = tag
                             }
                         }

                         fn nameOf(_ node: Node?) -> String {
                             if let node = node {
                                 return node.name
                             }
                             return "none"
                         }

                         fn describe(_ link: Link) -> String {
                             return nameOf(link.target) + " owned by " + link.owner.name
                         }
                         """;

    /// <summary>
    ///     A struct may hold a weak or an unowned reference: built memberwise
    ///     or by its own initialiser, read from a value or from where it is,
    ///     copied (each copy holds the object weakly in its own right) and
    ///     stored to. Nothing it holds that way keeps an object alive.
    /// </summary>
    [Fact]
    public void AStructsWeakAndUnownedFieldsHoldWithoutKeeping() =>
        Assert.Equal(
            "a owned by a\nb\na\nbye b\nnone\nnone\na\n3\na none\nbye a\n",
            AssertAgree(
                Program(
                    Links,
                    """
                        let a = Node("a")
                        var link = Link(target: a, owner: a)
                        print(describe(link))
                        var copy = link
                        if true {
                            let b = Node("b")
                            link.target = b
                            print(nameOf(link.target))
                            print(nameOf(copy.target))
                        }
                        print(nameOf(link.target))
                        copy.target = none
                        print(nameOf(copy.target))
                        let tagged = Tagged(a, 3)
                        print(nameOf(tagged.node))
                        print(tagged.tag)
                        let all = [tagged, Tagged(none, 4)]
                        print(nameOf(all[0].node) + " " + nameOf(all[1].node))
                    """
                )
            ).Stdout
        );

    /// <summary>A parent holding its children by value, each pointing back weakly: no cycle, so all of it goes.</summary>
    [Fact]
    public void AStructsWeakBackReferenceBreaksTheCycle() =>
        Assert.Equal(
            "home\n2\nfolder home closed\n",
            AssertAgree(
                Program(
                    """
                    class Folder {
                        var name: String
                        var entries: Entry[>..]
                        init(_ name: String) {
                            self.name = name
                            self.entries = []
                        }
                        deinit {
                            print("folder " + name + " closed")
                        }
                    }

                    struct Entry {
                        weak var folder: Folder?
                        var size: Int
                    }
                    """,
                    """
                        let home = Folder("home")
                        home.entries.append(Entry(folder: home, size: 1))
                        home.entries.append(Entry(folder: home, size: 2))
                        if let folder = home.entries[1].folder {
                            print(folder.name)
                        }
                        print(home.entries.count)
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AStructsUnownedFieldTrapsOnceItsObjectIsGone() {
        var execution = AssertAgree(
            Program(
                Links + """

                        fn dangling() -> Link {
                            let gone = Node("gone")
                            return Link(target: gone, owner: gone)
                        }
                        """,
                """
                    let link = dangling()
                    print(nameOf(link.target))
                    print(link.owner.name)
                """
            )
        );
        Assert.Equal("bye gone\nnone\n", execution.Stdout);
        Assert.StartsWith("fatal error: read an unowned reference to an object that is gone\n", execution.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("class C {}\nclass D { weak let c: C? }", "'weak' needs a 'var'")]
    [InlineData("class C {}\nclass D { weak var c: C }", "'weak' needs a 'var' of an optional class type")]
    [InlineData("class D { unowned var n: Int }", "'unowned' needs a class type")]
    public void MisplacedWeakAndUnownedAreErrors(string declarations, string message) {
        var result = Pipeline.Compile(new SourceText($"module M\n{declarations}\n@main\nfn main() {{ }}\n", "test.sb"));
        Assert.Contains(result.Errors, e => e.Contains(message, StringComparison.Ordinal));
    }
}

/// <summary>A local dies at the end of the block that declared it, not when its function returns.</summary>
public sealed class LifetimeTests {
    [Fact]
    public void LocalsDieWithTheirScope() =>
        Assert.Equal(
            "a made\na gone\nafter the block\nl0 made\nloop 0\nl0 gone\nl1 made\nloop 1\nl1 gone\nouter made\nearly made\nearly gone\nouter gone\nreturned 7\nlast made\nend of main\nlast gone\n",
            AssertAgree(
                Program(
                    """
                    class Noisy {
                        var name: String
                        init(_ name: String) {
                            self.name = name
                            print(name + " made")
                        }
                        deinit {
                            print(name + " gone")
                        }
                    }

                    fn early() -> Int {
                        let outer = Noisy("outer")
                        do {
                            let inner = Noisy("early")
                            if inner.name.count > 0 {
                                return 7
                            }
                        }
                        return 0
                    }
                    """,
                    """
                        do {
                            let a = Noisy("a")
                        }
                        print("after the block")
                        for i in 0..<2 {
                            let l = Noisy("l\(i)")
                            print("loop \(i)")
                        }
                        print("returned \(early())")
                        let last = Noisy("last")
                        print("end of main")
                    """
                )
            ).Stdout
        );
}
