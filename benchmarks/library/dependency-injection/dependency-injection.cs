using Microsoft.Extensions.DependencyInjection;

// Port of dependency-injection.sb.
[Benchmark("library/dependency-injection")]
static class LibraryDependencyInjection {
    sealed class N0(N1 n1, N2 n2) {
        public long Total() => 0 + n1.Total() + n2.Total();
    }

    sealed class N1(N3 n3, N4 n4) {
        public long Total() => 1 + n3.Total() + n4.Total();
    }

    sealed class N2(N5 n5, N6 n6) {
        public long Total() => 2 + n5.Total() + n6.Total();
    }

    sealed class N3(N7 n7, N8 n8) {
        public long Total() => 3 + n7.Total() + n8.Total();
    }

    sealed class N4(N9 n9, N10 n10) {
        public long Total() => 4 + n9.Total() + n10.Total();
    }

    sealed class N5(N11 n11, N12 n12) {
        public long Total() => 5 + n11.Total() + n12.Total();
    }

    sealed class N6(N13 n13, N14 n14) {
        public long Total() => 6 + n13.Total() + n14.Total();
    }

    sealed class N7(N15 n15, N16 n16) {
        public long Total() => 7 + n15.Total() + n16.Total();
    }

    sealed class N8(N17 n17, N18 n18) {
        public long Total() => 8 + n17.Total() + n18.Total();
    }

    sealed class N9(N19 n19, N20 n20) {
        public long Total() => 9 + n19.Total() + n20.Total();
    }

    sealed class N10(N21 n21, N22 n22) {
        public long Total() => 10 + n21.Total() + n22.Total();
    }

    sealed class N11(N23 n23, N24 n24) {
        public long Total() => 11 + n23.Total() + n24.Total();
    }

    sealed class N12(N25 n25, N26 n26) {
        public long Total() => 12 + n25.Total() + n26.Total();
    }

    sealed class N13(N27 n27, N28 n28) {
        public long Total() => 13 + n27.Total() + n28.Total();
    }

    sealed class N14(N29 n29, N30 n30) {
        public long Total() => 14 + n29.Total() + n30.Total();
    }

    sealed class N15(N31 n31, N32 n32) {
        public long Total() => 15 + n31.Total() + n32.Total();
    }

    sealed class N16(N33 n33, N34 n34) {
        public long Total() => 16 + n33.Total() + n34.Total();
    }

    sealed class N17(N35 n35, N36 n36) {
        public long Total() => 17 + n35.Total() + n36.Total();
    }

    sealed class N18(N37 n37, N38 n38) {
        public long Total() => 18 + n37.Total() + n38.Total();
    }

    sealed class N19(N39 n39, N40 n40) {
        public long Total() => 19 + n39.Total() + n40.Total();
    }

    sealed class N20(N41 n41, N42 n42) {
        public long Total() => 20 + n41.Total() + n42.Total();
    }

    sealed class N21(N43 n43, N44 n44) {
        public long Total() => 21 + n43.Total() + n44.Total();
    }

    sealed class N22(N45 n45, N46 n46) {
        public long Total() => 22 + n45.Total() + n46.Total();
    }

    sealed class N23(N47 n47, N48 n48) {
        public long Total() => 23 + n47.Total() + n48.Total();
    }

    sealed class N24(N49 n49) {
        public long Total() => 24 + n49.Total();
    }

    sealed class N25() {
        public long Total() => 25;
    }

    sealed class N26() {
        public long Total() => 26;
    }

    sealed class N27() {
        public long Total() => 27;
    }

    sealed class N28() {
        public long Total() => 28;
    }

    sealed class N29() {
        public long Total() => 29;
    }

    sealed class N30() {
        public long Total() => 30;
    }

    sealed class N31() {
        public long Total() => 31;
    }

    sealed class N32() {
        public long Total() => 32;
    }

    sealed class N33() {
        public long Total() => 33;
    }

    sealed class N34() {
        public long Total() => 34;
    }

    sealed class N35() {
        public long Total() => 35;
    }

    sealed class N36() {
        public long Total() => 36;
    }

    sealed class N37() {
        public long Total() => 37;
    }

    sealed class N38() {
        public long Total() => 38;
    }

    sealed class N39() {
        public long Total() => 39;
    }

    sealed class N40() {
        public long Total() => 40;
    }

    sealed class N41() {
        public long Total() => 41;
    }

    sealed class N42() {
        public long Total() => 42;
    }

    sealed class N43() {
        public long Total() => 43;
    }

    sealed class N44() {
        public long Total() => 44;
    }

    sealed class N45() {
        public long Total() => 45;
    }

    sealed class N46() {
        public long Total() => 46;
    }

    sealed class N47() {
        public long Total() => 47;
    }

    sealed class N48() {
        public long Total() => 48;
    }

    sealed class N49() {
        public long Total() => 49;
    }

    public static Func<string> Setup() {
        var services = new ServiceCollection();
        services.AddTransient<N0>();
        services.AddTransient<N1>();
        services.AddTransient<N2>();
        services.AddTransient<N3>();
        services.AddTransient<N4>();
        services.AddTransient<N5>();
        services.AddTransient<N6>();
        services.AddTransient<N7>();
        services.AddTransient<N8>();
        services.AddTransient<N9>();
        services.AddTransient<N10>();
        services.AddTransient<N11>();
        services.AddScoped<N12>();
        services.AddScoped<N13>();
        services.AddScoped<N14>();
        services.AddScoped<N15>();
        services.AddScoped<N16>();
        services.AddScoped<N17>();
        services.AddScoped<N18>();
        services.AddScoped<N19>();
        services.AddScoped<N20>();
        services.AddScoped<N21>();
        services.AddScoped<N22>();
        services.AddScoped<N23>();
        services.AddScoped<N24>();
        services.AddSingleton<N25>();
        services.AddSingleton<N26>();
        services.AddSingleton<N27>();
        services.AddSingleton<N28>();
        services.AddSingleton<N29>();
        services.AddSingleton<N30>();
        services.AddSingleton<N31>();
        services.AddSingleton<N32>();
        services.AddSingleton<N33>();
        services.AddSingleton<N34>();
        services.AddSingleton<N35>();
        services.AddSingleton<N36>();
        services.AddSingleton<N37>();
        services.AddSingleton<N38>();
        services.AddSingleton<N39>();
        services.AddSingleton<N40>();
        services.AddSingleton<N41>();
        services.AddSingleton<N42>();
        services.AddSingleton<N43>();
        services.AddSingleton<N44>();
        services.AddSingleton<N45>();
        services.AddSingleton<N46>();
        services.AddSingleton<N47>();
        services.AddSingleton<N48>();
        services.AddSingleton<N49>();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = false });
        return () => Run(provider);
    }

    static string Run(ServiceProvider provider) {
        long total = 0;
        for (var i = 0; i < 20000; i++) {
            using var scope = provider.CreateScope();
            total += scope.ServiceProvider.GetRequiredService<N0>().Total();
        }

        return $"{total}";
    }
}
