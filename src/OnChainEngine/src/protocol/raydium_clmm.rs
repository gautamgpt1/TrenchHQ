use thiserror::Error;

pub const PROGRAM_ID: &str = "CAMMCzo5YL8w4VFF8KVHrK22GGUsp5VTaW7grrKgrWqK";
pub const POOL_ACCOUNT_LEN: usize = 1544;
const POOL_DISCRIMINATOR: [u8; 8] = [247, 237, 227, 245, 215, 195, 222, 70];
const SWAP_DISCRIMINATOR: [u8; 8] = [248, 198, 158, 145, 225, 117, 135, 200];
const SWAP_V2_DISCRIMINATOR: [u8; 8] = [43, 4, 237, 11, 26, 201, 30, 98];
const SWAP_ROUTER_BASE_IN_DISCRIMINATOR: [u8; 8] = [69, 125, 115, 218, 245, 186, 242, 196];

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RaydiumClmmPoolState {
    pub token_0_mint: String,
    pub token_1_mint: String,
    pub token_0_vault: String,
    pub token_1_vault: String,
    pub mint_0_decimals: u8,
    pub mint_1_decimals: u8,
    pub tick_spacing: u16,
    pub liquidity: u128,
    pub sqrt_price_x64: u128,
    pub tick_current: i32,
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum RaydiumClmmDecodeError {
    #[error("account owner is not the Raydium CLMM program")]
    WrongOwner,
    #[error("account length is not the supported Raydium CLMM PoolState layout")]
    WrongLength,
    #[error("account discriminator is not a Raydium CLMM PoolState")]
    WrongDiscriminator,
}

pub fn is_swap_instruction(data: &[u8]) -> bool {
    data.starts_with(&SWAP_DISCRIMINATOR)
        || data.starts_with(&SWAP_V2_DISCRIMINATOR)
        || data.starts_with(&SWAP_ROUTER_BASE_IN_DISCRIMINATOR)
}

pub fn decode_pool(
    owner_program: &str,
    data: &[u8],
) -> Result<RaydiumClmmPoolState, RaydiumClmmDecodeError> {
    if owner_program != PROGRAM_ID {
        return Err(RaydiumClmmDecodeError::WrongOwner);
    }
    if data.len() != POOL_ACCOUNT_LEN {
        return Err(RaydiumClmmDecodeError::WrongLength);
    }
    if data[..8] != POOL_DISCRIMINATOR {
        return Err(RaydiumClmmDecodeError::WrongDiscriminator);
    }

    Ok(RaydiumClmmPoolState {
        token_0_mint: pubkey(data, 73),
        token_1_mint: pubkey(data, 105),
        token_0_vault: pubkey(data, 137),
        token_1_vault: pubkey(data, 169),
        mint_0_decimals: data[233],
        mint_1_decimals: data[234],
        tick_spacing: u16_at(data, 235),
        liquidity: u128_at(data, 237),
        sqrt_price_x64: u128_at(data, 253),
        tick_current: i32_at(data, 269),
    })
}

fn pubkey(data: &[u8], offset: usize) -> String {
    bs58::encode(&data[offset..offset + 32]).into_string()
}

fn u16_at(data: &[u8], offset: usize) -> u16 {
    u16::from_le_bytes(
        data[offset..offset + 2]
            .try_into()
            .expect("validated layout"),
    )
}

fn u128_at(data: &[u8], offset: usize) -> u128 {
    u128::from_le_bytes(
        data[offset..offset + 16]
            .try_into()
            .expect("validated layout"),
    )
}

fn i32_at(data: &[u8], offset: usize) -> i32 {
    i32::from_le_bytes(
        data[offset..offset + 4]
            .try_into()
            .expect("validated layout"),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decodes_q64_pool_fields_at_the_packed_offsets() {
        let mut data = vec![0_u8; POOL_ACCOUNT_LEN];
        data[..8].copy_from_slice(&POOL_DISCRIMINATOR);
        data[73..105].copy_from_slice(&[1; 32]);
        data[105..137].copy_from_slice(&[2; 32]);
        data[137..169].copy_from_slice(&[3; 32]);
        data[169..201].copy_from_slice(&[4; 32]);
        data[233] = 6;
        data[234] = 9;
        data[235..237].copy_from_slice(&64_u16.to_le_bytes());
        data[237..253].copy_from_slice(&123_u128.to_le_bytes());
        data[253..269].copy_from_slice(&(1_u128 << 64).to_le_bytes());
        data[269..273].copy_from_slice(&(-42_i32).to_le_bytes());

        let state = decode_pool(PROGRAM_ID, &data).unwrap();
        assert_eq!(state.sqrt_price_x64, 1_u128 << 64);
        assert_eq!(state.tick_current, -42);
        assert_eq!(state.tick_spacing, 64);
    }
}
