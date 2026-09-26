use crate::domain::DecimalValue;
use crate::price::{rational_quote_per_base, PriceMathError, DEFAULT_PRICE_SCALE};
use num_bigint::BigUint;
use std::cmp::Ordering;
use thiserror::Error;

pub const PROTOCOL_ID: &str = "aerodrome-classic";
pub const BASE_FACTORY: &str = "0x420dd381b31aef6683db6b902084cb0ffece40da";
pub const SWAP_TOPIC: &str = "0xb3e2773606abfd36b5bd91394b3a54d1398336c65005baf7bf7a05efeffaf75b";
pub const SYNC_TOPIC: &str = "0xcf2aa50876cdfbb541206f89af0ee78d44a2abf8d328e37fa4917f982149848a";
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
    #[error("Aerodrome classic swap topics are malformed")]
    InvalidTopics,
    #[error("Aerodrome classic ABI data is malformed")]
    InvalidData,
    #[error("Aerodrome classic swap deltas are zero or ambiguous")]
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
    if words[0].bits() > 256 || words[1].bits() > 256 || words[2].bits() > 256 {
        return Err(DecodeError::InvalidData);
    }
    Ok(Reserves {
        reserve0: words[0].clone(),
        reserve1: words[1].clone(),
    })
}

pub fn decode_sync(data: &str) -> Result<Reserves, DecodeError> {
    let words = decode_words(data, 2)?;
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

pub fn volatile_spot_price(
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

pub fn stable_spot_price(
    reserves: &Reserves,
    selected_is_token0: bool,
    token0_decimals: u8,
    token1_decimals: u8,
) -> Result<DecimalValue, DecodeError> {
    if reserves.reserve0 == BigUint::from(0_u8) || reserves.reserve1 == BigUint::from(0_u8) {
        return Err(DecodeError::InvalidData);
    }
    let common_decimals = token0_decimals.max(token1_decimals);
    let x =
        &reserves.reserve0 * BigUint::from(10_u8).pow(u32::from(common_decimals - token0_decimals));
    let y =
        &reserves.reserve1 * BigUint::from(10_u8).pow(u32::from(common_decimals - token1_decimals));
    let x_squared = &x * &x;
    let y_squared = &y * &y;
    let three = BigUint::from(3_u8);
    let token1_numerator = &y * (&three * &x_squared + &y_squared);
    let token1_denominator = &x * (&x_squared + &three * &y_squared);
    let (quote, base) = if selected_is_token0 {
        (token1_numerator, token1_denominator)
    } else {
        (token1_denominator, token1_numerator)
    };
    Ok(rational_quote_per_base(
        quote,
        base,
        0,
        0,
        DEFAULT_PRICE_SCALE,
    )?)
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
    fn swap_price_uses_aerodrome_topic_and_exact_amounts() {
        let topics = vec![
            SWAP_TOPIC.to_owned(),
            "0xsender".to_owned(),
            "0xto".to_owned(),
        ];
        let swap = decode_swap(
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
            executed_price(&swap, true, 18, 6).unwrap().coefficient,
            "2000000000000000000000"
        );
    }

    #[test]
    fn volatile_spot_is_decimal_normalized_reserve_ratio() {
        let reserves = Reserves {
            reserve0: BigUint::from(2_u8) * BigUint::from(10_u8).pow(18),
            reserve1: BigUint::from(4_000_000_u64),
        };
        assert_eq!(
            volatile_spot_price(&reserves, true, 18, 6)
                .unwrap()
                .coefficient,
            "2000000000000000000"
        );
    }

    #[test]
    fn stable_spot_handles_peg_imbalance_inversion_and_decimal_mismatch() {
        let peg = Reserves {
            reserve0: BigUint::from(2_u8) * BigUint::from(10_u8).pow(18),
            reserve1: BigUint::from(2_000_000_u64),
        };
        assert_eq!(
            stable_spot_price(&peg, true, 18, 6).unwrap().coefficient,
            "1000000000000000000"
        );

        let imbalanced = Reserves {
            reserve0: BigUint::from(2_u8) * BigUint::from(10_u8).pow(18),
            reserve1: BigUint::from(1_000_000_u64),
        };
        let forward = stable_spot_price(&imbalanced, true, 18, 6).unwrap();
        let inverse = stable_spot_price(&imbalanced, false, 18, 6).unwrap();
        assert_eq!(forward.coefficient, "928571428571428571");
        assert_eq!(inverse.coefficient, "1076923076923076923");
    }

    #[test]
    fn stable_spot_rejects_zero_and_accepts_full_width_reserves() {
        let zero = Reserves {
            reserve0: BigUint::from(0_u8),
            reserve1: BigUint::from(1_u8),
        };
        assert_eq!(
            stable_spot_price(&zero, true, 18, 6),
            Err(DecodeError::InvalidData)
        );

        let large = Reserves {
            reserve0: (BigUint::from(1_u8) << 255) - BigUint::from(1_u8),
            reserve1: (BigUint::from(1_u8) << 254) - BigUint::from(1_u8),
        };
        assert!(!stable_spot_price(&large, true, 18, 18)
            .unwrap()
            .coefficient
            .is_empty());
    }
}
