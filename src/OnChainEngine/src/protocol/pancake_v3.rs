use crate::domain::DecimalValue;
use crate::price::{
    rational_quote_per_base, sqrt_price_x96_spot_price, PriceMathError, DEFAULT_PRICE_SCALE,
};
use num_bigint::{BigInt, BigUint, Sign};
use thiserror::Error;

pub const PROTOCOL_ID: &str = "pancake-v3";
pub const BNB_FACTORY: &str = "0x0bfbcf9fa4f9c56b0f40a671ad40e0805a091865";
pub const SWAP_TOPIC: &str = "0x19b47279256b2a23a1665c810c8d55a1758940ee09377d4f8d26497a3577dc83";
pub const SLOT0_CALL_ID: &str = "slot0";
pub const LIQUIDITY_CALL_ID: &str = "liquidity";

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Swap {
    pub amount0: BigInt,
    pub amount1: BigInt,
    pub sqrt_price_x96: BigUint,
    pub liquidity: BigUint,
    pub tick: i32,
    pub protocol_fees_token0: BigUint,
    pub protocol_fees_token1: BigUint,
}

pub type Slot0 = crate::protocol::uniswap_v3::Slot0;

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum DecodeError {
    #[error("PancakeSwap v3 swap topics are malformed")]
    InvalidTopics,
    #[error("PancakeSwap v3 ABI data is malformed")]
    InvalidData,
    #[error("PancakeSwap v3 swap deltas are zero or have the same sign")]
    AmbiguousSwap,
    #[error(transparent)]
    Price(#[from] PriceMathError),
}

pub fn decode_swap(topics: &[String], data: &str) -> Result<Swap, DecodeError> {
    if topics.len() != 3 || !topics[0].eq_ignore_ascii_case(SWAP_TOPIC) {
        return Err(DecodeError::InvalidTopics);
    }
    let words = decode_words(data, 7)?;
    let amount0 = signed_256(&words[0]);
    let amount1 = signed_256(&words[1]);
    let sqrt_price_x96 = words[2].clone();
    let liquidity = words[3].clone();
    let tick = signed_small(&words[4], 24)?;
    if sqrt_price_x96 == BigUint::from(0_u8)
        || sqrt_price_x96.bits() > 160
        || liquidity.bits() > 128
        || !(-887_272..=887_272).contains(&tick)
        || words[5].bits() > 128
        || words[6].bits() > 128
    {
        return Err(DecodeError::InvalidData);
    }
    Ok(Swap {
        amount0,
        amount1,
        sqrt_price_x96,
        liquidity,
        tick,
        protocol_fees_token0: words[5].clone(),
        protocol_fees_token1: words[6].clone(),
    })
}

pub fn decode_slot0(data: &str) -> Result<Slot0, DecodeError> {
    crate::protocol::uniswap_v3::decode_slot0(data).map_err(|_| DecodeError::InvalidData)
}

pub fn decode_liquidity(data: &str) -> Result<BigUint, DecodeError> {
    crate::protocol::uniswap_v3::decode_liquidity(data).map_err(|_| DecodeError::InvalidData)
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

    #[test]
    fn seven_word_swap_decodes_extended_protocol_fees() {
        let q96 = BigUint::from(1_u8) << 96;
        let data = format!(
            "0x{}{}{}{}{}{}{}",
            signed_word(2_000_000_000_000_000_000),
            signed_word(-4_000_000),
            unsigned_word(&q96),
            unsigned_word(&BigUint::from(100_u8)),
            signed_word(0),
            unsigned_word(&BigUint::from(3_u8)),
            unsigned_word(&BigUint::from(4_u8))
        );
        let topics = vec![
            SWAP_TOPIC.to_owned(),
            "0xsender".to_owned(),
            "0xto".to_owned(),
        ];
        let swap = decode_swap(&topics, &data).unwrap();
        assert_eq!(swap.protocol_fees_token0, BigUint::from(3_u8));
        assert_eq!(swap.protocol_fees_token1, BigUint::from(4_u8));
        assert_eq!(
            executed_price(&swap, true, 18, 6).unwrap().coefficient,
            "2000000000000000000"
        );
    }

    #[test]
    fn uniswap_v3_five_word_payload_is_rejected() {
        assert_eq!(
            decode_swap(
                &[
                    SWAP_TOPIC.to_owned(),
                    "0xsender".to_owned(),
                    "0xto".to_owned()
                ],
                &format!("0x{}", "0".repeat(64 * 5))
            ),
            Err(DecodeError::InvalidData)
        );
    }
}
