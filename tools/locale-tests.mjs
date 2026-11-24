// Writes tests/lib/Sable.Globalization/data/: what Node's Intl, which is ICU,
// writes and orders, for Sable.Globalization's tests to be held to. Node must be
// of the ICU that tools/locale-tables.py was given (process.versions.icu):
//
//     node tools/locale-tests.mjs
//
// Intl reads ICU's data by its own way, not the tables the script makes, so
// this is a check of the script as well as of the library. Left out is what
// the library does not do as ICU does: zone names, other calendars, the Han
// order. Rounding is asked of Intl as half to even, the library's.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), "..");
const out = path.join(root, "tests/lib/Sable.Globalization/data");
fs.mkdirSync(out, { recursive: true });
const tables = fs.readFileSync(path.join(root, "lib/Sable.Globalization/LocaleTables.sb"), "utf8");
const tags = /const localeTags = """\n([^"]*)"""/.exec(tables)[1].trim().split(/\s+/);
const others = ["zh-TW", "nb-NO", "ar-EG-u-nu-latn", "hi-u-nu-deva", "th-u-nu-thai", "bn-u-nu-latn"];

const write = (name, lines) => {
    const header = `# Written by tools/locale-tests.mjs from Node's Intl (ICU ${process.versions.icu}, CLDR ${process.versions.cldr}); do not edit.`;
    fs.writeFileSync(path.join(out, name), [header, ...lines].join("\n") + "\n");
    console.log(name, lines.length);
};

// ---- Numbers -----------------------------------------------------------------

const values = ["0", "1", "-1", "1234", "12345", "-1234567.891", "0.5", "1234567890123", "0.0625", "99.995"];
const numbers = [];
for (const tag of [...tags, ...others]) {
    for (const value of values) {
        const add = (format, options) =>
            numbers.push([tag, format, value, new Intl.NumberFormat(tag, { roundingMode: "halfEven", ...options }).format(Number(value))].join("\t"));
        add("decimal", {});
        if (["1", "12345", "-1", "0.5"].includes(value)) {
            add("percent", { style: "percent", maximumFractionDigits: 1 });
            add("scientific", { notation: "scientific" });
        }
        if (["12345", "-1234567.891", "1", "0.5"].includes(value)) {
            for (const currency of ["EUR", "USD", "JPY", "INR", "CHF", "KWD", "CZK"]) {
                add(`currency:${currency}`, { style: "currency", currency });
            }
            add("accounting:USD", { style: "currency", currency: "USD", currencySign: "accounting" });
            add("code:USD", { style: "currency", currency: "USD", currencyDisplay: "code" });
            add("narrow:CAD", { style: "currency", currency: "CAD", currencyDisplay: "narrowSymbol" });
            add("signed", { signDisplay: "always", maximumFractionDigits: 2 });
        }
    }
}
write("numbers.txt", numbers);

// ---- Plurals -----------------------------------------------------------------

const plurals = [];
for (const tag of tags) {
    for (const type of ["cardinal", "ordinal"]) {
        const rules = new Intl.PluralRules(tag, { type });
        const line = [];
        for (let i = 0; i <= 130; i++) {
            line.push(`${i}=${rules.select(i)}`);
        }
        for (const i of [200, 202, 1000, 1000000, 2000000, 1000001]) {
            line.push(`${i}=${rules.select(i)}`);
        }
        if (type === "cardinal") {
            for (const i of [0.5, 1.5, 2.1, 10.2, 0.11]) {
                line.push(`${i}=${rules.select(i)}`);
            }
        }
        plurals.push([tag, type, ...line].join("\t"));
    }
}
write("plurals.txt", plurals);

// ---- Lists -------------------------------------------------------------------

const lists = [];
for (const tag of tags) {
    for (const type of ["conjunction", "disjunction", "unit"]) {
        for (const style of ["long", "short", "narrow"]) {
            const formatter = new Intl.ListFormat(tag, { type, style });
            for (const items of [["A"], ["A", "B"], ["A", "B", "C"], ["A", "B", "C", "D"]]) {
                lists.push([tag, type, style, items.join("|"), formatter.format(items)].join("\t"));
            }
        }
    }
}
for (const items of [["uno", "idea"], ["x", "hielo"], ["x", "hijo"], ["siete", "ocho"], ["7", "8"], ["10", "11"], ["a", "oro"]]) {
    for (const type of ["conjunction", "disjunction"]) {
        lists.push(["es", type, "long", items.join("|"), new Intl.ListFormat("es", { type }).format(items)].join("\t"));
    }
}
for (const items of [["א", "ב"], ["א", "B"], ["א", "ב", "3"]]) {
    lists.push(["he", "conjunction", "long", items.join("|"), new Intl.ListFormat("he").format(items)].join("\t"));
}
write("lists.txt", lists);

// ---- Dates -------------------------------------------------------------------

const dates = [];
const moments = ["2026-11-17T09:05:03Z", "2024-02-29T00:30:00Z", "1999-08-01T13:45:59Z"];
const styles = ["full", "long", "medium", "short"];
for (const tag of [...tags, ...others]) {
    for (const moment of moments) {
        const add = (dateStyle, timeStyle) => {
            const text = new Intl.DateTimeFormat(tag, { calendar: "gregory", timeZone: "UTC", dateStyle, timeStyle }).format(new Date(moment));
            dates.push([tag, dateStyle ?? "-", timeStyle ?? "-", moment.slice(0, -1), text].join("\t"));
        };
        for (const date of styles) {
            add(date, undefined);
            add(date, "short");
        }
        add(undefined, "medium");
        add(undefined, "short");
    }
}
write("dates.txt", dates);

// ---- Collation ---------------------------------------------------------------

let seed = 12345;
const random = n => {
    seed = (Math.imul(seed, 1103515245) + 12345) & 0x7fffffff;
    return (seed >>> 8) % n;
};
const alphabets = {
    latin: [..."aAbBcCdDeEzZsStTuUvVwWxXyYnNlLjJhHiIgGpPkK", ..."äÄöÖüÜåÅæÆøØœßẞþÞðÐñÑçÇčČćĆžŽšŠđĐłŁżŻźŹśŚńŃóÓąĄęĘ",
        ..."áéíóúýàèìòùâêîôûãẽõőűŐŰğĞışİĺľťďřŘůŮŭạảấầặắđớờ", "ch", "Ch", "CH", "ll", "Ll", "dž", "Dž", "lj", "nj", "cs", "dzs", "gy", "ly", "ny", "sz", "ty", "zs",
        "ng", "ff", "rh", "th", "aa", "Aa", "AA", "ä", "ộ", "ậ", "š", "i̇", "ȩ́", "ĳ"],
    cyrillic: [..."аАбБвВгГґҐдДеЕёЁжЖзЗиИіІїЇйЙкКлЛмМнНђЂљЉњЊћЋџЏэЭюЮяЯьЬъЪ", "й", "ё", "ѓ"],
    greek: [..."αΑάΆβΒγΓδεέηήθικλμνξοόπρσςτυύϋΰφχψωώ", "ά"],
    hebrew: [..."אבגדהוזחטיכךלמםנןסעפףצץקרשת", "שׁ", "בּ"],
    arabic: [..."ابتثجحخدذرزسشصضطظعغفقكلمنهويىةآأإؤئءپچژگکی", "آ", "بَ", "ﻻ"],
    devanagari: [..."अआइईउऊएऐओऔकखगघचछजझटठडढणतथदधनपफबभमयरलवशषसहंः", "क्ष", "का", "कि", "क़", "ं"],
    bengali: [..."অআইঈকখগঘচছজঝটঠডঢণতথদধনপফবভমযরলশষসহংঃৎ", "ড়"],
    tamil: [..."அஆஇஈஉஊகஙசஞடணதநபமயரலவழளறன", "க்", "கா", "கௌ", "ஜ", "ஸ"],
    thai: [..."กขคฆงจฉชซดตถทนบปผฝพฟภมยรลวศษสหอ", "เก", "แข", "โค", "ไท", "ก่", "ก้า", "ฯ", "ๆ"],
    kana: [..."あアいイうウかカがガきキさサざザたタっッはハばバぱパんンーゝ", "ｱ", "㌀"],
    hangul: [..."가각간갇힣나다라마바사아자차카타파하", "ᄀ", "ᅡ"],
    marks: [..." -_,;:!?.'\"()[]{}@*/\\&#%`^+<=>|~$€£¥0123456789²½Ⅷ①", "…", "–", "—", "‘", "’", "«", "­", "​", "٣", "५", "ﬁ", "ª", "™", "℃"],
};
const scripts = { ru: ["cyrillic"], uk: ["cyrillic"], bg: ["cyrillic"], sr: ["cyrillic"], el: ["greek"], he: ["hebrew"], ar: ["arabic"], fa: ["arabic"],
    hi: ["devanagari"], mr: ["devanagari"], bn: ["bengali", "devanagari"], ta: ["tamil", "devanagari"], th: ["thai"] };
const collationTables = fs.readFileSync(path.join(root, "lib/Sable.Globalization/CollationTables.sb"), "utf8");
const collations = ["und", "en", "de", "fr", ...new Set([...collationTables.matchAll(/case "([^"]*)" => tailoring/g)].map(m => m[1]))];
const collation = [];
for (const tag of collations) {
    const language = tag.split("-")[0];
    const own = scripts[language] ?? ["latin"];
    const pools = tag === "und" ? Object.keys(alphabets) : [...own, ...own, ...own, "latin", "marks"];
    const words = new Set(["", "a", "A", "b"]);
    const count = tag === "und" ? 900 : 160;
    while (words.size < count) {
        const pool = alphabets[pools[random(pools.length)]];
        let word = "";
        for (let n = 1 + random(4); n > 0; n--) {
            const from = random(5) === 0 ? alphabets[pools[random(pools.length)]] : pool;
            word += from[random(from.length)];
        }
        words.add(word);
    }
    for (const [name, options] of [["", {}], ["base", { sensitivity: "base" }], ["accent", { sensitivity: "accent" }], ["numeric", { numeric: true }],
        ["upper", { caseFirst: "upper" }], ["lower", { caseFirst: "lower" }], ["ignore", { ignorePunctuation: true }]]) {
        if (name !== "" && !["und", "sv", "da", "ru"].includes(tag)) {
            continue;
        }
        const collator = new Intl.Collator(tag, options);
        const sorted = [...words].sort((a, b) => collator.compare(a, b));
        const line = [sorted[0]];
        for (let i = 1; i < sorted.length; i++) {
            line.push((collator.compare(sorted[i - 1], sorted[i]) < 0 ? "<" : "=") + sorted[i]);
        }
        collation.push([tag, name, ...line].join("\t"));
    }
}
write("collation.txt", collation);
