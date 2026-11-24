// Port of math.sb.
#include <math.h>
#include "../../support/c/checked.h"

int main(void) {
    double total = 0.0;
    for (int i = 1; i < 500001; i++) {
        double x = i / 1000.0;
        total += sin(x) + cos(x) + log(x) + exp(-x) + atan2(x, 3.0) + hypot(x, 2.0);
    }
    printf("%.4f\n", total);
    return 0;
}
