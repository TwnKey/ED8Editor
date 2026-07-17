use tocs::tbl::AtomicValue as Value;
use tocs::tbl::Attribute;

pub enum RowFilter {
    /// No filter, matches all rows
    PassAll,
    /// Doesn't match any row, show none
    DenyAll,
    /// Attribute contains substring, with case insensitive comparison.
    ///
    /// If attribute is None, any attribute can match
    CaseInsensitiveContains { attribute: Option<Attribute>, substring: regex::Regex },
    /// Attribute value is greater/lesser than the reference - only for numeric values
    Compare { attribute: Attribute, operator: Operator, reference: Value },
}

#[derive(Clone, Copy, Debug)]
pub enum Operator {
    Eq,
    Gt,
    Lt,
}

impl Operator {
    fn compare<T: PartialOrd>(self, a: T, b: T) -> bool {
        match self {
            Operator::Eq => a == b,
            Operator::Gt => a > b,
            Operator::Lt => a < b,
        }
    }
}

impl std::str::FromStr for Operator {
    type Err = ();
    fn from_str(s: &str) -> Result<Self, Self::Err> {
        match s {
            ">" => Ok(Operator::Gt),
            "<" => Ok(Operator::Lt),
            "=" => Ok(Operator::Eq),
            _ => Err(()),
        }
    }
}

fn to_case_insensitive_search_regex(s: &str) -> regex::Regex {
    regex::RegexBuilder::new(&regex::escape(s)).case_insensitive(true).build().expect("escaped regex should be valid")
}

impl RowFilter {
    pub fn from_str(s: &str, schema: &tocs::tbl::AtomicSchema) -> RowFilter {
        // attr=___ -> check attribute equality
        // attr>___ -> attribute comparison
        // attr<___ -> attribute comparison
        // attr:___ -> check attribute contains
        // anything else: check any attribute contains

        // find the first occurence of an operator char
        let Some((i, op_char)) = s.chars().enumerate().find(|(_, c)| [':', '=', '<', '>'].contains(c)) else {
            // no operator => search full substring
            if s.is_empty() {
                // search string is empty => all rows pass
                return RowFilter::PassAll;
            } else {
                return RowFilter::CaseInsensitiveContains {
                    attribute: None,
                    substring: to_case_insensitive_search_regex(s),
                };
            }
        };
        let attribute = Attribute::from(&s[..i]);
        let string = &s[i + 1..]; // All operator chars are ASCII, so they are encoded as a single byte which means the next byte index is the start of the next char.
        let Some(r#type) = schema.get(&attribute) else {
            // attribute does not exist in schema => no row can pass
            return RowFilter::DenyAll;
        };
        if op_char == ':' {
            // substring search just for the one attribute
            RowFilter::CaseInsensitiveContains {
                attribute: Some(attribute),
                substring: to_case_insensitive_search_regex(string),
            }
        } else {
            match Value::from_str(r#type, string) {
                Err(_) => {
                    // invalid value for the type => no row can pass
                    RowFilter::DenyAll
                }
                Ok(reference) => RowFilter::Compare {
                    attribute,
                    operator: s[i..i + 1].parse().expect("only valid op_char values are possible"),
                    reference,
                },
            }
        }
    }
}

pub fn row_passes_filter(entry: &tocs::tbl::Entry, filter: &RowFilter) -> bool {
    match filter {
        RowFilter::PassAll => true,
        RowFilter::DenyAll => false,
        RowFilter::CaseInsensitiveContains { attribute, substring } => {
            if let Some(attr) = attribute {
                entry.values.get(attr).map(|v| substring.is_match(ecow::eco_format!("{v}").as_str())).unwrap_or(false)
            } else {
                entry.values.values().any(|v| substring.is_match(ecow::eco_format!("{v}").as_str()))
            }
        }
        RowFilter::Compare { attribute, operator, reference } => {
            let Some(value) = entry.values.get(attribute) else { return false };
            match (value, reference) {
                (Value::U8(a), Value::U8(b)) => operator.compare(a, b),
                (Value::U16(a), Value::U16(b)) => operator.compare(a, b),
                (Value::U32(a), Value::U32(b)) => operator.compare(a, b),
                (Value::I8(a), Value::I8(b)) => operator.compare(a, b),
                (Value::I16(a), Value::I16(b)) => operator.compare(a, b),
                (Value::I32(a), Value::I32(b)) => operator.compare(a, b),
                (Value::F32(a), Value::F32(b)) => operator.compare(a, b),
                (Value::CUtf8(a), Value::CUtf8(b)) => operator.compare(a, b),
                (Value::X(a), Value::X(b)) => operator.compare(a, b),
                _ => unreachable!("Compare filter should only be built for attributes with correct type"),
            }
        }
    }
}