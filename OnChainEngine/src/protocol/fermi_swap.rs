use crate::domain::DecimalValue;
use crate::price::{rational_quote_per_base, PriceMathError, DEFAULT_PRICE_SCALE};
use num_bigint::BigUint;
use sha2::{Digest, Sha256};
use thiserror::Error;

pub const PROTOCOL_ID: &str = "fermi-swap";
pub const LEGACY_SWAPPER: &str = "0xb1076fe3ab5e28005c7c323bac5ac06a680d452e";
pub const CURRENT_SWAPPER: &str = "0x5979458912f80b96d30d4220af8e2e4925a33320";
pub const FERMI_SWAP_TOPIC: &str =
    "0x58d77aee9b6d4a0709dc53a9b14e40fd6283c950843fced71fbb7639f1235ba4";
pub const SWAPPED_TOPIC: &str =
    "0x1eeaa4acf3c225a4033105c2647625dbb298dec93b14e16253c4231e26c02b1d";

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Swap {
    pub token_in: String,
    pub token_out: String,
    pub amount_in: BigUint,
    pub amount_out: BigUint,
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum DecodeError {
    #[error("FermiSwap topics are malformed")]
    InvalidTopics,
    #[error("FermiSwap ABI data is malformed")]
    InvalidData,
    #[error("FermiSwap amounts are zero")]
    ZeroAmount,
    #[error("FermiSwap event does not match the selected pair")]
    WrongPair,
    #[error(transparent)]
    Price(#[from] PriceMathError),
}

pub fn is_swap_topic(topic: &str) -> bool {
    topic.eq_ignore_ascii_case(FERMI_SWAP_TOPIC) || topic.eq_ignore_ascii_case(SWAPPED_TOPIC)
}

pub fn matches_pair(topics: &[String], base: &str, quote: &str) -> bool {
    if topics.len() != 4 || !is_swap_topic(&topics[0]) {
        return false;
    }
    let (Some(token_in), Some(token_out)) = (
        decode_topic_address(&topics[2]),
        decode_topic_address(&topics[3]),
    ) else {
        return false;
    };
    (token_in.eq_ignore_ascii_case(base) && token_out.eq_ignore_ascii_case(quote))
        || (token_in.eq_ignore_ascii_case(quote) && token_out.eq_ignore_ascii_case(base))
}

pub fn decode_swap(topics: &[String], data: &str) -> Result<Swap, DecodeError> {
    if topics.len() != 4 || !is_swap_topic(&topics[0]) {
        return Err(DecodeError::InvalidTopics);
    }
    let token_in = decode_topic_address(&topics[2]).ok_or(DecodeError::InvalidTopics)?;
    let token_out = decode_topic_address(&topics[3]).ok_or(DecodeError::InvalidTopics)?;
    if token_in == token_out {
        return Err(DecodeError::InvalidTopics);
    }
    let word_count = if topics[0].eq_ignore_ascii_case(SWAPPED_TOPIC) {
        3
    } else {
        2
    };
    let words = decode_words(data, word_count)?;
    if word_count == 3 && decode_word_address(data, 2).is_none() {
        return Err(DecodeError::InvalidData);
    }
    if words[0] == BigUint::from(0_u8) || words[1] == BigUint::from(0_u8) {
        return Err(DecodeError::ZeroAmount);
    }
    Ok(Swap {
        token_in,
        token_out,
        amount_in: words[0].clone(),
        amount_out: words[1].clone(),
    })
}

pub fn executed_price(
    swap: &Swap,
    base: &str,
    quote: &str,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, DecodeError> {
    let (base_amount, quote_amount) = if swap.token_in.eq_ignore_ascii_case(base)
        && swap.token_out.eq_ignore_ascii_case(quote)
    {
        (swap.amount_in.clone(), swap.amount_out.clone())
    } else if swap.token_in.eq_ignore_ascii_case(quote) && swap.token_out.eq_ignore_ascii_case(base)
    {
        (swap.amount_out.clone(), swap.amount_in.clone())
    } else {
        return Err(DecodeError::WrongPair);
    };
    Ok(rational_quote_per_base(
        quote_amount,
        base_amount,
        base_decimals,
        quote_decimals,
        DEFAULT_PRICE_SCALE,
    )?)
}

pub fn pair_id(contract: &str, base: &str, quote: &str) -> String {
    let mut tokens = [base.to_ascii_lowercase(), quote.to_ascii_lowercase()];
    tokens.sort();
    let identity = format!(
        "{}:{}:{}",
        contract.to_ascii_lowercase(),
        tokens[0],
        tokens[1]
    );
    format!("0x{:x}", Sha256::digest(identity.as_bytes()))
}

fn decode_topic_address(topic: &str) -> Option<String> {
    if topic.len() != 66
        || !topic.starts_with("0x")
        || !topic.as_bytes()[2..]
            .iter()
            .all(|byte| byte.is_ascii_hexdigit())
        || topic.as_bytes()[2..26].iter().any(|byte| *byte != b'0')
    {
        return None;
    }
    Some(format!("0x{}", topic[26..].to_ascii_lowercase()))
}

fn decode_word_address(data: &str, index: usize) -> Option<String> {
    let start = 2 + index * 64;
    data.get(start..start + 64)
        .and_then(|word| decode_topic_address(&format!("0x{word}")))
}

fn decode_words(data: &str, count: usize) -> Result<Vec<BigUint>, DecodeError> {
    if data.len() != 2 + count * 64
        || !data.starts_with("0x")
        || !data.as_bytes()[2..]
            .iter()
            .all(|byte| byte.is_ascii_hexdigit())
    {
        return Err(DecodeError::InvalidData);
    }
    (0..count)
        .map(|index| {
            let start = 2 + index * 64;
            BigUint::parse_bytes(&data.as_bytes()[start..start + 64], 16)
                .ok_or(DecodeError::InvalidData)
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn word(value: u128) -> String {
        format!("{value:064x}")
    }

    fn topic_address(address: &str) -> String {
        format!("0x{:0>64}", &address[2..])
    }

    #[test]
    fn both_deployments_decode_and_price_both_directions() {
        let base = "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2";
        let quote = "0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48";
        let legacy = decode_swap(
            &[
                FERMI_SWAP_TOPIC.to_owned(),
                topic_address("0x1111111111111111111111111111111111111111"),
                topic_address(base),
                topic_address(quote),
            ],
            &format!(
                "0x{}{}",
                word(2_000_000_000_000_000_000),
                word(4_000_000_000)
            ),
        )
        .unwrap();
        assert_eq!(
            executed_price(&legacy, base, quote, 18, 6)
                .unwrap()
                .coefficient,
            "2000000000000000000000"
        );

        let current = decode_swap(
            &[
                SWAPPED_TOPIC.to_owned(),
                topic_address("0x1111111111111111111111111111111111111111"),
                topic_address(quote),
                topic_address(base),
            ],
            &format!(
                "0x{}{}{}",
                word(4_000_000_000),
                word(2_000_000_000_000_000_000),
                &topic_address("0x2222222222222222222222222222222222222222")[2..]
            ),
        )
        .unwrap();
        assert_eq!(
            executed_price(&current, base, quote, 18, 6)
                .unwrap()
                .coefficient,
            "2000000000000000000000"
        );
    }

    #[test]
    fn pair_identity_is_orientation_independent_and_events_fail_closed() {
        let base = "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2";
        let quote = "0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48";
        assert_eq!(
            pair_id(CURRENT_SWAPPER, base, quote),
            pair_id(CURRENT_SWAPPER, quote, base)
        );
        assert_eq!(
            pair_id(CURRENT_SWAPPER, base, quote),
            "0xc954555cf554e837f0314ed6a6c4d6372db48b21978d33695c3548b8d1fb6f59"
        );
        assert_eq!(
            decode_swap(
                &[
                    FERMI_SWAP_TOPIC.to_owned(),
                    topic_address("0x1111111111111111111111111111111111111111"),
                    topic_address(base),
                    topic_address(base),
                ],
                &format!("0x{}{}", word(1), word(1))
            ),
            Err(DecodeError::InvalidTopics)
        );
    }
}
