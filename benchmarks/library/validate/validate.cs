using System.ComponentModel.DataAnnotations;

// Port of validate.sb, with System.ComponentModel.DataAnnotations.
[Benchmark("library/validate")]
static class LibraryValidate {
    sealed class Customer {
        [Required, StringLength(50, MinimumLength = 2)]
        public string Name { get; init; } = "";

        [Required, EmailAddress]
        public string Email { get; init; } = "";

        [Range(18, 130)]
        public int Age { get; init; }

        [RegularExpression("^[A-Z]{2}[0-9]{4}$")]
        public string Code { get; init; } = "";

        [Required, StringLength(20)]
        public string? City { get; init; }
    }

    public static string Run() {
        checked {
            var failures = 0;
            var text = 0;
            for (var i = 0; i < 200000; i++) {
                var bad = i % 10 == 0;
                var customer = new Customer {
                    Name = "Ada Lovelace", Email = bad ? "ada.example.org" : "ada@example.org", Age = bad ? i % 18 : 18 + i % 100,
                    Code = "GB1815", City = "London",
                };
                var results = new List<ValidationResult>();
                Validator.TryValidateObject(customer, new ValidationContext(customer), results, true);
                foreach (var result in results) {
                    foreach (var name in result.MemberNames) {
                        failures++;
                        text += name.Length;
                    }
                }
            }

            return $"{failures} {text}";
        }
    }
}
