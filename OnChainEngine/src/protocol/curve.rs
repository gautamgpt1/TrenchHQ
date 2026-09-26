use crate::domain::DecimalValue;
use crate::price::{rational_quote_per_base, PriceMathError, DEFAULT_PRICE_SCALE};
use num_bigint::BigUint;
use thiserror::Error;

pub const PROTOCOL_ID: &str = "curve";
pub const MAINNET_ADDRESS_PROVIDER: &str = "0x0000000022d53366457f9d5e68ec105046fc4383";
pub const TOKEN_EXCHANGE_SIGNED_TOPIC: &str =
    "0x8b3e96f2b889fa771c53c981b40daf005f63f637f1869f707052d15a3dd97140";
pub const TOKEN_EXCHANGE_UNSIGNED_TOPIC: &str =
    "0xb2e76ae99761dc136e598d4a629bb347eccb9532a5f8bbd72e18467c3c34cc98";
pub const TOKEN_EXCHANGE_EXTENDED_TOPIC: &str =
    "0x143f1f8e861fbdeddd5b46e844b7d3ac7b86a122f36e8c463859ee6811b1f29c";

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TokenExchange {
    pub sold_id: u32,
    pub tokens_sold: BigUint,
    pub bought_id: u32,
    pub tokens_bought: BigUint,
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum DecodeError {
    #[error("Curve TokenExchange topics are malformed")]
    InvalidTopics,
    #[error("Curve TokenExchange ABI data is malformed")]
    InvalidData,
    #[error("Curve TokenExchange amounts are zero")]
    ZeroAmount,
    #[error(transparent)]
    Price(#[from] PriceMathError),
}

pub fn is_token_exchange_topic(topic: &str) -> bool {
    topic.eq_ignore_ascii_case(TOKEN_EXCHANGE_SIGNED_TOPIC)
        || topic.eq_ignore_ascii_case(TOKEN_EXCHANGE_UNSIGNED_TOPIC)
        || topic.eq_ignore_ascii_case(TOKEN_EXCHANGE_EXTENDED_TOPIC)
}

pub fn decode_token_exchange(topics: &[String], data: &str) -> Result<TokenExchange, DecodeError> {
    if topics.len() != 2 || !is_token_exchange_topic(&topics[0]) || !is_topic_word(&topics[1]) {
        return Err(DecodeError::InvalidTopics);
    }
    let word_count = if topics[0].eq_ignore_ascii_case(TOKEN_EXCHANGE_EXTENDED_TOPIC) {
        6
    } else {
        4
    };
    let words = decode_words(data, word_count)?;
    let sold_id = parse_index(&words[0])?;
    let bought_id = parse_index(&words[2])?;
    if sold_id == bought_id || words[1] == BigUint::from(0_u8) || words[3] == BigUint::from(0_u8) {
        return Err(DecodeError::ZeroAmount);
    }
    Ok(TokenExchange {
        sold_id,
        tokens_sold: words[1].clone(),
        bought_id,
        tokens_bought: words[3].clone(),
    })
}

pub fn executed_price(
    exchange: &TokenExchange,
    base_index: u32,
    quote_index: u32,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<Option<DecimalValue>, DecodeError> {
    let (base, quote) = if exchange.sold_id == base_index && exchange.bought_id == quote_index {
        (exchange.tokens_sold.clone(), exchange.tokens_bought.clone())
    } else if exchange.sold_id == quote_index && exchange.bought_id == base_index {
        (exchange.tokens_bought.clone(), exchange.tokens_sold.clone())
    } else {
        return Ok(None);
    };
    Ok(Some(rational_quote_per_base(
        quote,
        base,
        base_decimals,
        quote_decimals,
        DEFAULT_PRICE_SCALE,
    )?))
}

fn parse_index(word: &BigUint) -> Result<u32, DecodeError> {
    let digits = word.to_u32_digits();
    if word.bits() > 31 || digits.len() > 1 {
        return Err(DecodeError::InvalidData);
    }
    let value = digits.first().copied().unwrap_or(0);
    (value <= 7)
        .then_some(value)
        .ok_or(DecodeError::InvalidData)
}

fn is_topic_word(value: &str) -> bool {
    value.len() == 66
        && value.starts_with("0x")
        && value.as_bytes()[2..]
            .iter()
            .all(|byte| byte.is_ascii_hexdigit())
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

    #[test]
    fn signed_and_unsigned_events_price_both_pair_directions() {
        for topic in [TOKEN_EXCHANGE_SIGNED_TOPIC, TOKEN_EXCHANGE_UNSIGNED_TOPIC] {
            let forward = decode_token_exchange(
                &[topic.to_owned(), format!("0x{}", word(1))],
                &format!(
                    "0x{}{}{}{}",
                    word(0),
                    word(2_000_000_000_000_000_000),
                    word(1),
                    word(4_000_000_000)
                ),
            )
            .unwrap();
            assert_eq!(
                executed_price(&forward, 0, 1, 18, 6)
                    .unwrap()
                    .unwrap()
                    .coefficient,
                "2000000000000000000000"
            );

            let reverse = TokenExchange {
                sold_id: 1,
                tokens_sold: BigUint::from(4_000_000_000_u64),
                bought_id: 0,
                tokens_bought: BigUint::from(2_000_000_000_000_000_000_u64),
            };
            assert_eq!(
                executed_price(&reverse, 0, 1, 18, 6)
                    .unwrap()
                    .unwrap()
                    .coefficient,
                "2000000000000000000000"
            );
        }
        let extended = decode_token_exchange(
            &[
                TOKEN_EXCHANGE_EXTENDED_TOPIC.to_owned(),
                format!("0x{}", word(1)),
            ],
            &format!(
                "0x{}{}{}{}{}{}",
                word(0),
                word(2_000_000_000_000_000_000),
                word(1),
                word(4_000_000_000),
                word(4_000_000),
                word(1)
            ),
        )
        .unwrap();
        assert_eq!(
            executed_price(&extended, 0, 1, 18, 6)
                .unwrap()
                .unwrap()
                .coefficient,
            "2000000000000000000000"
        );
    }

    #[test]
    fn unrelated_pairs_are_ignored_and_malformed_events_fail_closed() {
        let exchange = TokenExchange {
            sold_id: 0,
            tokens_sold: BigUint::from(1_u8),
            bought_id: 2,
            tokens_bought: BigUint::from(1_u8),
        };
        assert_eq!(executed_price(&exchange, 0, 1, 18, 6).unwrap(), None);
        assert_eq!(
            decode_token_exchange(
                &[TOKEN_EXCHANGE_SIGNED_TOPIC.to_owned()],
                &format!("0x{}{}{}{}", word(0), word(1), word(1), word(1))
            ),
            Err(DecodeError::InvalidTopics)
        );
        assert_eq!(
            decode_token_exchange(
                &[
                    TOKEN_EXCHANGE_SIGNED_TOPIC.to_owned(),
                    format!("0x{}", word(1))
                ],
                &format!("0x{}{}{}{}", word(8), word(1), word(1), word(1))
            ),
            Err(DecodeError::InvalidData)
        );
    }
}
