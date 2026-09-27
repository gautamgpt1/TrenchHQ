use crate::domain::DecimalValue;
use crate::price::{
    rational_quote_per_base, sqrt_price_x96_spot_price, PriceMathError, DEFAULT_PRICE_SCALE,
};
use num_bigint::{BigInt, BigUint, Sign};
use thiserror::Error;

pub const PROTOCOL_ID: &str = "uniswap-v3";
pub const MAINNET_FACTORY: &str = "0x1f98431c8ad98523631ae4a59f267346ea31f984";
pub const SHIBASWAP_V2_FACTORY: &str = "0xd9ce49caf7299daf18fffcb2b84a44fd33412509";
pub const SWAP_TOPIC: &str = "0xc42079f94a6350d7e6235f29174924f928cc2ac818eb64fed8004e115fbcca67";
pub const SLOT0_CALL_ID: &str = "slot0";
pub const LIQUIDITY_CALL_ID: &str = "liquidity";

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Swap {
    pub amount0: BigInt,
    pub amount1: BigInt,
    pub sqrt_price_x96: BigUint,
    pub liquidity: BigUint,
    pub tick: i32,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Slot0 {
    pub sqrt_price_x96: BigUint,
    pub tick: i32,
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum DecodeError {
    #[error("Uniswap v3 swap topics are malformed")]
    InvalidTopics,
    #[error("Uniswap v3 ABI data is malformed")]
    InvalidData,
    #[error("Uniswap v3 swap deltas are zero or have the same sign")]
    AmbiguousSwap,
    #[error(transparent)]
    Price(#[from] PriceMathError),
}

pub fn decode_swap(topics: &[String], data: &str) -> Result<Swap, DecodeError> {
    if topics.len() != 3 || !topics[0].eq_ignore_ascii_case(SWAP_TOPIC) {
        return Err(DecodeError::InvalidTopics);
    }
    let words = decode_words(data, 5)?;
    let amount0 = signed_256(&words[0]);
    let amount1 = signed_256(&words[1]);
    let sqrt_price_x96 = words[2].clone();
    let liquidity = words[3].clone();
    let tick = signed_small(&words[4], 24)?;
    if sqrt_price_x96 == BigUint::from(0_u8)
        || sqrt_price_x96.bits() > 160
        || liquidity.bits() > 128
        || !(-887_272..=887_272).contains(&tick)
    {
        return Err(DecodeError::InvalidData);
    }
    Ok(Swap {
        amount0,
        amount1,
        sqrt_price_x96,
        liquidity,
        tick,
    })
}

pub fn decode_slot0(data: &str) -> Result<Slot0, DecodeError> {
    decode_slot0_words(data, 7)
}

pub(crate) fn decode_slot0_words(data: &str, word_count: usize) -> Result<Slot0, DecodeError> {
    let words = decode_words(data, word_count)?;
    let sqrt_price_x96 = words[0].clone();
    let tick = signed_small(&words[1], 24)?;
    if sqrt_price_x96 == BigUint::from(0_u8)
        || sqrt_price_x96.bits() > 160
        || !(-887_272..=887_272).contains(&tick)
    {
        return Err(DecodeError::InvalidData);
    }
    Ok(Slot0 {
        sqrt_price_x96,
        tick,
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

fn signed_256(word: &BigUint) -> BigInt {
    if word.bit(255) {
        BigInt::from_biguint(Sign::Plus, word.clone()) - (BigInt::from(1) << 256)
    } else {
        BigInt::from_biguint(Sign::Plus, word.clone())
    }
}

fn signed_small(word: &BigUint, bits: u32) -> Result<i32, DecodeError> {
    let value = signed_256(word);
    let limit = BigInt::from(1) << (bits - 1);
    if value < -&limit || value >= limit {
        return Err(DecodeError::InvalidData);
    }
    value
        .to_string()
        .parse::<i32>()
        .map_err(|_| DecodeError::InvalidData)
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
            "0xsender".to_owned(),
            "0xto".to_owned(),
        ]
    }

    #[test]
    fn swap_decodes_signed_deltas_and_q96_spot() {
        let q96 = BigUint::from(1_u8) << 96;
        let data = format!(
            "0x{}{}{}{}{}",
            signed_word(2_000_000_000_000_000_000),
            signed_word(-4_000_000),
            unsigned_word(&q96),
            unsigned_word(&BigUint::from(100_u8)),
            signed_word(0)
        );
        let swap = decode_swap(&topics(), &data).unwrap();
        assert_eq!(swap.amount1, BigInt::from(-4_000_000));
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
    fn inversion_uses_integer_rational_math() {
        let q96 = BigUint::from(2_u8) << 96;
        assert_eq!(
            spot_price(&q96, false, 18, 18).unwrap().coefficient,
            "250000000000000000"
        );
    }

    #[test]
    fn malformed_or_same_sign_swaps_fail_closed() {
        assert_eq!(
            decode_swap(&topics(), "0x00"),
            Err(DecodeError::InvalidData)
        );
        let q96 = BigUint::from(1_u8) << 96;
        let data = format!(
            "0x{}{}{}{}{}",
            signed_word(1),
            signed_word(1),
            unsigned_word(&q96),
            unsigned_word(&BigUint::from(1_u8)),
            signed_word(0)
        );
        let swap = decode_swap(&topics(), &data).unwrap();
        assert_eq!(
            executed_price(&swap, true, 18, 18),
            Err(DecodeError::AmbiguousSwap)
        );
    }
}
