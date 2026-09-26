use crate::domain::DecimalValue;
use crate::price::{
    rational_quote_per_base, sqrt_price_x96_spot_price, PriceMathError, DEFAULT_PRICE_SCALE,
};
use num_bigint::{BigInt, BigUint, Sign};
use thiserror::Error;

pub const PROTOCOL_ID: &str = "uniswap-v4";
pub const MAINNET_POOL_MANAGER: &str = "0x000000000004444c5dc75cb358380d2e3de08a90";
pub const SWAP_TOPIC: &str = "0x40e9cecb9f5f1f1c5b9c97dec2917b7ee92e57ba5563708daca94dd84ad7112f";
pub const SLOT0_CALL_ID: &str = "slot0";
pub const LIQUIDITY_CALL_ID: &str = "liquidity";

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Swap {
    pub pool_id: String,
    pub amount0: BigInt,
    pub amount1: BigInt,
    pub sqrt_price_x96: BigUint,
    pub liquidity: BigUint,
    pub tick: i32,
    pub fee: u32,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Slot0 {
    pub sqrt_price_x96: BigUint,
    pub tick: i32,
    pub protocol_fee: u32,
    pub lp_fee: u32,
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum DecodeError {
    #[error("Uniswap v4 swap topics are malformed")]
    InvalidTopics,
    #[error("Uniswap v4 ABI data is malformed")]
    InvalidData,
    #[error("Uniswap v4 swap deltas are zero or have the same sign")]
    AmbiguousSwap,
    #[error(transparent)]
    Price(#[from] PriceMathError),
}

pub fn decode_swap(topics: &[String], data: &str) -> Result<Swap, DecodeError> {
    if topics.len() != 3
        || !topics[0].eq_ignore_ascii_case(SWAP_TOPIC)
        || !is_pool_id(&topics[1])
        || !is_indexed_address(&topics[2])
    {
        return Err(DecodeError::InvalidTopics);
    }
    let words = decode_words(data, 6)?;
    let amount0 = signed_small(&words[0], 128)?;
    let amount1 = signed_small(&words[1], 128)?;
    let sqrt_price_x96 = words[2].clone();
    let liquidity = words[3].clone();
    let tick = signed_small(&words[4], 24)?
        .to_string()
        .parse::<i32>()
        .map_err(|_| DecodeError::InvalidData)?;
    let fee = words[5]
        .to_string()
        .parse::<u32>()
        .map_err(|_| DecodeError::InvalidData)?;
    if sqrt_price_x96 == BigUint::from(0_u8)
        || sqrt_price_x96.bits() > 160
        || liquidity.bits() > 128
        || !(-887_272..=887_272).contains(&tick)
        || words[5].bits() > 24
    {
        return Err(DecodeError::InvalidData);
    }
    Ok(Swap {
        pool_id: topics[1].to_ascii_lowercase(),
        amount0,
        amount1,
        sqrt_price_x96,
        liquidity,
        tick,
        fee,
    })
}

pub fn decode_slot0(data: &str) -> Result<Slot0, DecodeError> {
    let words = decode_words(data, 4)?;
    let sqrt_price_x96 = words[0].clone();
    let tick = signed_small(&words[1], 24)?
        .to_string()
        .parse::<i32>()
        .map_err(|_| DecodeError::InvalidData)?;
    if sqrt_price_x96 == BigUint::from(0_u8)
        || sqrt_price_x96.bits() > 160
        || !(-887_272..=887_272).contains(&tick)
        || words[2].bits() > 24
        || words[3].bits() > 24
    {
        return Err(DecodeError::InvalidData);
    }
    Ok(Slot0 {
        sqrt_price_x96,
        tick,
        protocol_fee: words[2]
            .to_string()
            .parse::<u32>()
            .map_err(|_| DecodeError::InvalidData)?,
        lp_fee: words[3]
            .to_string()
            .parse::<u32>()
            .map_err(|_| DecodeError::InvalidData)?,
    })
}

pub fn decode_liquidity(data: &str) -> Result<BigUint, DecodeError> {
    let words = decode_words(data, 1)?;
    if words[0].bits() > 128 {
        return Err(DecodeError::InvalidData);
    }
    Ok(words[0].clone())
}

pub fn executed_price(
    swap: &Swap,
    selected_is_token0: bool,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, DecodeError> {
    if swap.amount0 == BigInt::from(0)
        || swap.amount1 == BigInt::from(0)
        || swap.amount0.sign() == swap.amount1.sign()
    {
        return Err(DecodeError::AmbiguousSwap);
    }
    let (base, quote) = if selected_is_token0 {
        (
            swap.amount0.magnitude().clone(),
            swap.amount1.magnitude().clone(),
        )
    } else {
        (
            swap.amount1.magnitude().clone(),
            swap.amount0.magnitude().clone(),
        )
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
    sqrt_price_x96: &BigUint,
    selected_is_token0: bool,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, DecodeError> {
    Ok(sqrt_price_x96_spot_price(
        sqrt_price_x96.clone(),
        selected_is_token0,
        base_decimals,
        quote_decimals,
    )?)
}

pub fn is_pool_id(value: &str) -> bool {
    value.len() == 66
        && value.starts_with("0x")
        && value.as_bytes()[2..]
            .iter()
            .all(|byte| byte.is_ascii_hexdigit())
}

pub fn hook_returns_swap_delta(value: &str) -> bool {
    if value.len() != 42
        || !value.starts_with("0x")
        || !value.as_bytes()[2..]
            .iter()
            .all(|byte| byte.is_ascii_hexdigit())
    {
        return true;
    }
    u8::from_str_radix(&value[value.len() - 2..], 16).map_or(true, |flags| flags & 0x0c != 0)
}

fn is_indexed_address(value: &str) -> bool {
    is_pool_id(value) && value.as_bytes()[2..26].iter().all(|byte| *byte == b'0')
}

fn signed_small(word: &BigUint, bits: u32) -> Result<BigInt, DecodeError> {
    let value = if word.bit(255) {
        BigInt::from_biguint(Sign::Plus, word.clone()) - (BigInt::from(1) << 256)
    } else {
        BigInt::from_biguint(Sign::Plus, word.clone())
    };
    let limit = BigInt::from(1) << (bits - 1);
    if value < -&limit || value >= limit {
        return Err(DecodeError::InvalidData);
    }
    Ok(value)
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

    fn unsigned_word(value: &BigUint) -> String {
        format!("{value:0>64x}")
    }

    fn signed_word(value: i128) -> String {
        let encoded = if value < 0 {
            (BigInt::from(1) << 256) + BigInt::from(value)
        } else {
            BigInt::from(value)
        };
        format!("{:0>64x}", encoded.to_biguint().unwrap())
    }

    fn topics() -> Vec<String> {
        vec![
            SWAP_TOPIC.to_owned(),
            format!("0x{:064x}", 7),
            format!("0x{:064x}", 8),
        ]
    }

    #[test]
    fn swap_decodes_pool_id_deltas_and_state() {
        let q96 = BigUint::from(1_u8) << 96;
        let data = format!(
            "0x{}{}{}{}{}{}",
            signed_word(2_000_000_000_000_000_000),
            signed_word(-4_000_000),
            unsigned_word(&q96),
            unsigned_word(&BigUint::from(100_u8)),
            signed_word(0),
            unsigned_word(&BigUint::from(500_u16))
        );
        let swap = decode_swap(&topics(), &data).unwrap();
        assert_eq!(swap.pool_id, topics()[1]);
        assert_eq!(swap.fee, 500);
        assert_eq!(
            executed_price(&swap, true, 18, 6).unwrap().coefficient,
            "2000000000000000000"
        );
        assert_eq!(
            spot_price(&q96, true, 18, 6).unwrap().coefficient,
            "1000000000000000000000000000000"
        );
    }

    #[test]
    fn swap_delta_hook_permissions_fail_closed() {
        assert!(!hook_returns_swap_delta(
            "0x0000000000000000000000000000000000000000"
        ));
        assert!(!hook_returns_swap_delta(
            "0x0000000000000000000000000000000000000080"
        ));
        assert!(hook_returns_swap_delta(
            "0x0000000000000000000000000000000000000008"
        ));
        assert!(hook_returns_swap_delta(
            "0x0000000000000000000000000000000000000004"
        ));
        assert!(hook_returns_swap_delta("not-an-address"));
    }

    #[test]
    fn state_view_slot0_has_four_words() {
        let q96 = BigUint::from(1_u8) << 96;
        let data = format!(
            "0x{}{}{}{}",
            unsigned_word(&q96),
            signed_word(-10),
            unsigned_word(&BigUint::from(12_u8)),
            unsigned_word(&BigUint::from(3000_u16))
        );
        let slot0 = decode_slot0(&data).unwrap();
        assert_eq!(slot0.tick, -10);
        assert_eq!(slot0.lp_fee, 3000);
    }

    #[test]
    fn malformed_identity_and_same_sign_deltas_are_rejected() {
        let q96 = BigUint::from(1_u8) << 96;
        let data = format!(
            "0x{}{}{}{}{}{}",
            signed_word(2),
            signed_word(4),
            unsigned_word(&q96),
            unsigned_word(&BigUint::from(1_u8)),
            signed_word(0),
            unsigned_word(&BigUint::from(500_u16))
        );
        let swap = decode_swap(&topics(), &data).unwrap();
        assert_eq!(
            executed_price(&swap, true, 18, 18),
            Err(DecodeError::AmbiguousSwap)
        );
        let mut bad_topics = topics();
        bad_topics[1] = "0x1234".to_owned();
        assert_eq!(
            decode_swap(&bad_topics, &data),
            Err(DecodeError::InvalidTopics)
        );
    }
}
