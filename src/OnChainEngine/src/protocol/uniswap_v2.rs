use crate::domain::DecimalValue;
use crate::price::{rational_quote_per_base, PriceMathError, DEFAULT_PRICE_SCALE};
use num_bigint::BigUint;
use std::cmp::Ordering;
use thiserror::Error;

pub const PROTOCOL_ID: &str = "uniswap-v2";
pub const MAINNET_FACTORY: &str = "0x5c69bee701ef814a2b6a3edd4b1652cb9cc5aa6f";
pub const SHIBASWAP_V1_FACTORY: &str = "0x115934131916c8b277dd010ee02de363c09d037c";
pub const SWAP_TOPIC: &str = "0xd78ad95fa46c994b6551d0da85fc275fe613ce37657fb8d5e3d130840159d822";
pub const SYNC_TOPIC: &str = "0x1c411e9a96e071241c2f21f7726b17ae89e3cab4c78be50e062b03a9fffbbad1";
pub const RESERVES_CALL_ID: &str = "getReserves";

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Swap {
    pub amount0_in: BigUint,
    pub amount1_in: BigUint,
    pub amount0_out: BigUint,
    pub amount1_out: BigUint,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Reserves {
    pub reserve0: BigUint,
    pub reserve1: BigUint,
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum DecodeError {
    #[error("Uniswap v2 swap topics are malformed")]
    InvalidTopics,
    #[error("Uniswap v2 ABI data is malformed")]
    InvalidData,
    #[error("Uniswap v2 swap deltas are zero or ambiguous")]
    AmbiguousSwap,
    #[error(transparent)]
    Price(#[from] PriceMathError),
}

pub fn decode_swap(topics: &[String], data: &str) -> Result<Swap, DecodeError> {
    if topics.len() != 3 || !topics[0].eq_ignore_ascii_case(SWAP_TOPIC) {
        return Err(DecodeError::InvalidTopics);
    }
    let words = decode_words(data, 4)?;
    Ok(Swap {
        amount0_in: words[0].clone(),
        amount1_in: words[1].clone(),
        amount0_out: words[2].clone(),
        amount1_out: words[3].clone(),
    })
}

pub fn decode_reserves(data: &str) -> Result<Reserves, DecodeError> {
    let words = decode_words(data, 3)?;
    if words[0].bits() > 112 || words[1].bits() > 112 || words[2].bits() > 32 {
        return Err(DecodeError::InvalidData);
    }
    Ok(Reserves {
        reserve0: words[0].clone(),
        reserve1: words[1].clone(),
    })
}

pub fn decode_sync(data: &str) -> Result<Reserves, DecodeError> {
    let words = decode_words(data, 2)?;
    if words[0].bits() > 112 || words[1].bits() > 112 {
        return Err(DecodeError::InvalidData);
    }
    Ok(Reserves {
        reserve0: words[0].clone(),
        reserve1: words[1].clone(),
    })
}

pub fn executed_price(
    swap: &Swap,
    selected_is_token0: bool,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, DecodeError> {
    let token0 = net_delta(&swap.amount0_in, &swap.amount0_out)?;
    let token1 = net_delta(&swap.amount1_in, &swap.amount1_out)?;
    if token0.ordering == token1.ordering {
        return Err(DecodeError::AmbiguousSwap);
    }
    let (base, quote) = if selected_is_token0 {
        (token0.amount, token1.amount)
    } else {
        (token1.amount, token0.amount)
    };
    Ok(rational_quote_per_base(
        quote,
        base,
        base_decimals,
        quote_decimals,
        DEFAULT_PRICE_SCALE,
    )?)
}

pub fn spot_price(
    reserves: &Reserves,
    selected_is_token0: bool,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, DecodeError> {
    let (base, quote) = if selected_is_token0 {
        (reserves.reserve0.clone(), reserves.reserve1.clone())
    } else {
        (reserves.reserve1.clone(), reserves.reserve0.clone())
    };
    Ok(rational_quote_per_base(
        quote,
        base,
        base_decimals,
        quote_decimals,
        DEFAULT_PRICE_SCALE,
    )?)
}

pub fn is_address(value: &str) -> bool {
    value.len() == 42
        && value.starts_with("0x")
        && value.as_bytes()[2..]
            .iter()
            .all(|byte| byte.is_ascii_hexdigit())
}

struct NetDelta {
    ordering: Ordering,
    amount: BigUint,
}

fn net_delta(amount_in: &BigUint, amount_out: &BigUint) -> Result<NetDelta, DecodeError> {
    match amount_in.cmp(amount_out) {
        Ordering::Greater => Ok(NetDelta {
            ordering: Ordering::Greater,
            amount: amount_in - amount_out,
        }),
        Ordering::Less => Ok(NetDelta {
            ordering: Ordering::Less,
            amount: amount_out - amount_in,
        }),
        Ordering::Equal => Err(DecodeError::AmbiguousSwap),
    }
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
    fn swap_price_handles_both_directions_and_decimal_orientation() {
        let topics = vec![
            SWAP_TOPIC.to_owned(),
            "0xsender".to_owned(),
            "0xto".to_owned(),
        ];
        let token0_in = decode_swap(
            &topics,
            &format!(
                "0x{}{}{}{}",
                word(2_000_000_000_000_000_000),
                word(0),
                word(0),
                word(4_000_000_000)
            ),
        )
        .unwrap();
        assert_eq!(
            executed_price(&token0_in, true, 18, 6).unwrap().coefficient,
            "2000000000000000000000"
        );

        let token1_in = decode_swap(
            &topics,
            &format!(
                "0x{}{}{}{}",
                word(0),
                word(4_000_000_000),
                word(2_000_000_000_000_000_000),
                word(0)
            ),
        )
        .unwrap();
        assert_eq!(
            executed_price(&token1_in, true, 18, 6).unwrap().coefficient,
            "2000000000000000000000"
        );
    }

    #[test]
    fn reserves_are_oriented_and_decimal_corrected() {
        let reserves = decode_reserves(&format!(
            "0x{}{}{}",
            word(10_000_000_000_000_000_000),
            word(20_000_000_000),
            word(123)
        ))
        .unwrap();
        assert_eq!(
            spot_price(&reserves, true, 18, 6).unwrap().coefficient,
            "2000000000000000000000"
        );
    }

    #[test]
    fn malformed_or_ambiguous_swaps_fail_closed() {
        let topics = vec![
            SWAP_TOPIC.to_owned(),
            "0xsender".to_owned(),
            "0xto".to_owned(),
        ];
        assert_eq!(decode_swap(&topics, "0x00"), Err(DecodeError::InvalidData));
        let ambiguous = decode_swap(
            &topics,
            &format!("0x{}{}{}{}", word(1), word(1), word(0), word(0)),
        )
        .unwrap();
        assert_eq!(
            executed_price(&ambiguous, true, 18, 18),
            Err(DecodeError::AmbiguousSwap)
        );
    }
}
