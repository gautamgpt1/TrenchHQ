use crate::domain::DecimalValue;
use crate::price::{rational_quote_per_base, PriceMathError, DEFAULT_PRICE_SCALE};
use num_bigint::BigUint;
use thiserror::Error;

pub const PROTOCOL_ID: &str = "pons-v2-curve";
pub const ROBINHOOD_FACTORY: &str = "0x7ed598bcef8bd9edd8c97a195c6d13f40801ec7e";
pub const BUY_TOPIC: &str = "0xec36bf571f136799e8dc0b0b8bea4b04d8bd3d43de838aab0d5fc21d4cbfc455";
pub const SELL_TOPIC: &str = "0x8113d738abdcb6b38357e9d53a54a7157861a09031b453651f0fe7fe151f59df";
pub const RESERVES_CALL_ID: &str = "getReserves";

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Trade {
    pub base_amount: BigUint,
    pub quote_amount: BigUint,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Reserves {
    pub quote_reserve: BigUint,
    pub token_reserve: BigUint,
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum DecodeError {
    #[error("pons v2 curve topics are malformed")]
    InvalidTopics,
    #[error("pons v2 curve ABI data is malformed")]
    InvalidData,
    #[error("pons v2 curve trade has a zero amount")]
    ZeroAmount,
    #[error(transparent)]
    Price(#[from] PriceMathError),
}

pub fn is_trade_topic(topic: &str) -> bool {
    topic.eq_ignore_ascii_case(BUY_TOPIC) || topic.eq_ignore_ascii_case(SELL_TOPIC)
}

pub fn decode_trade(topics: &[String], data: &str) -> Result<Trade, DecodeError> {
    if topics.len() != 3 || topics.first().is_none_or(|topic| !is_trade_topic(topic)) {
        return Err(DecodeError::InvalidTopics);
    }
    let words = decode_words(data, 4)?;
    let (base_amount, quote_amount) = if topics[0].eq_ignore_ascii_case(BUY_TOPIC) {
        (words[1].clone(), words[0].clone())
    } else {
        (words[0].clone(), words[1].clone())
    };
    if base_amount == BigUint::from(0_u8) || quote_amount == BigUint::from(0_u8) {
        return Err(DecodeError::ZeroAmount);
    }
    Ok(Trade {
        base_amount,
        quote_amount,
    })
}

pub fn decode_reserves(data: &str) -> Result<Reserves, DecodeError> {
    let words = decode_words(data, 2)?;
    if words[0] == BigUint::from(0_u8) || words[1] == BigUint::from(0_u8) {
        return Err(DecodeError::InvalidData);
    }
    Ok(Reserves {
        quote_reserve: words[0].clone(),
        token_reserve: words[1].clone(),
    })
}

pub fn executed_price(
    trade: &Trade,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, DecodeError> {
    Ok(rational_quote_per_base(
        trade.quote_amount.clone(),
        trade.base_amount.clone(),
        base_decimals,
        quote_decimals,
        DEFAULT_PRICE_SCALE,
    )?)
}

pub fn spot_price(
    reserves: &Reserves,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, DecodeError> {
    Ok(rational_quote_per_base(
        reserves.quote_reserve.clone(),
        reserves.token_reserve.clone(),
        base_decimals,
        quote_decimals,
        DEFAULT_PRICE_SCALE,
    )?)
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
    fn buy_and_sell_use_event_settled_amounts() {
        let buy = decode_trade(
            &[
                BUY_TOPIC.to_owned(),
                "0xbuyer".to_owned(),
                "0xrecipient".to_owned(),
            ],
            &format!(
                "0x{}{}{}{}",
                word(4_000_000),
                word(2_000_000_000_000_000_000),
                word(1),
                word(2)
            ),
        )
        .unwrap();
        assert_eq!(
            executed_price(&buy, 18, 6).unwrap().coefficient,
            "2000000000000000000"
        );

        let sell = decode_trade(
            &[
                SELL_TOPIC.to_owned(),
                "0xseller".to_owned(),
                "0xrecipient".to_owned(),
            ],
            &format!(
                "0x{}{}{}{}",
                word(2_000_000_000_000_000_000),
                word(4_000_000),
                word(1),
                word(2)
            ),
        )
        .unwrap();
        assert_eq!(
            executed_price(&sell, 18, 6).unwrap(),
            executed_price(&buy, 18, 6).unwrap()
        );
    }

    #[test]
    fn reserves_are_quote_then_token() {
        let reserves = decode_reserves(&format!(
            "0x{}{}",
            word(20_000_000),
            word(10_000_000_000_000_000_000)
        ))
        .unwrap();
        assert_eq!(
            spot_price(&reserves, 18, 6).unwrap().coefficient,
            "2000000000000000000"
        );
    }
}
