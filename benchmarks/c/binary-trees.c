// Manually-managed port of benchmarks/binary-trees.sb (see README.md).
//
// One malloc per Node, as one Sable class instance is one heap object; each
// tree is freed when the Sable program lets it go (right after check), the
// long-lived one at exit. Int arithmetic is checked, as Sable's is.

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

static _Noreturn void trap(const char *message) {
    fflush(stdout);
    fprintf(stderr, "fatal error: %s\n", message);
    abort();
}

static inline int64_t add_i(int64_t a, int64_t b) {
    int64_t r;
    if (__builtin_add_overflow(a, b, &r)) trap("integer overflow");
    return r;
}

static inline int64_t sub_i(int64_t a, int64_t b) {
    int64_t r;
    if (__builtin_sub_overflow(a, b, &r)) trap("integer overflow");
    return r;
}

typedef struct Node {
    struct Node *left;
    struct Node *right;
} Node;

static Node *node_new(Node *left, Node *right) {
    Node *node = malloc(sizeof(Node));
    if (node == NULL) trap("out of memory");
    node->left = left;
    node->right = right;
    return node;
}

static void node_free(Node *node) {
    if (node->left != NULL) node_free(node->left);
    if (node->right != NULL) node_free(node->right);
    free(node);
}

static Node *build(int64_t depth) {
    if (depth == 0) {
        return node_new(NULL, NULL);
    }
    Node *left = build(sub_i(depth, 1));
    Node *right = build(sub_i(depth, 1));
    return node_new(left, right);
}

static int64_t check(const Node *node) {
    int64_t total = 1;
    if (node->left != NULL) {
        total = add_i(total, check(node->left));
    }
    if (node->right != NULL) {
        total = add_i(total, check(node->right));
    }
    return total;
}

int main(void) {
    const int64_t maxDepth = 16;
    Node *longLived = build(maxDepth);

    int64_t depth = 4;
    while (depth <= maxDepth) {
        int64_t iterations = (int64_t)1 << add_i(sub_i(maxDepth, depth), 4);
        int64_t sum = 0;
        int64_t i = 0;
        while (i < iterations) {
            Node *tree = build(depth);
            sum = add_i(sum, check(tree));
            node_free(tree);
            i = add_i(i, 1);
        }
        printf("%lld trees of depth %lld: %lld\n", (long long)iterations, (long long)depth, (long long)sum);
        depth = add_i(depth, 2);
    }

    printf("long lived tree of depth %lld: %lld\n", (long long)maxDepth, (long long)check(longLived));
    node_free(longLived);
    return 0;
}
