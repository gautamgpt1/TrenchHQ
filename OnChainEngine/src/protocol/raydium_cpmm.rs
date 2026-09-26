use thiserror::Error;

pub const PROGRAM_ID: &str = "CPMMoo8L3F4NbTegBCKVNunggL7H1ZpdTHKxQB5qKP1C";
pub const POOL_ACCOUNT_LEN: usize = 637;
const POOL_DISCRIMINATOR: [u8; 8] = [247, 237, 227, 245, 215, 195, 222, 70];
const SWAP_BASE_INPUT_DISCRIMINATOR: [u8; 8] = [143, 190, 90, 218, 196, 30, 51, 222];
const SWAP_BASE_OUTPUT_DISCRIMINATOR: [u8; 8] = [55, 217, 98, 86, 163, 74, 180, 173];

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RaydiumCpmmPoolState {
    pub token_0_vault: String,
    pub token_1_vault: String,
    pub token_0_mint: String,
    pub token_1_mint: String,
    pub token_0_program: String,
    pub token_1_program: String,
    pub mint_0_decimals: u8,
    pub mint_1_decimals: u8,
    pub protocol_fees_token_0: u64,
    pub protocol_fees_token_1: u64,
    pub fund_fees_token_0: u64,
    pub fund_fees_token_1: u64,
    pub creator_fees_token_0: u64,
    pub creator_fees_token_1: u64,
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum RaydiumCpmmDecodeError {
    #[error("account owner is not the Raydium CPMM program")]
    WrongOwner,
    #[error("account length is not the supported Raydium CPMM PoolState layout")]
    WrongLength,
    #[error("account discriminator is not a Raydium CPMM PoolState")]
    WrongDiscriminator,
}

pub fn is_swap_instruction(data: &[u8]) -> bool {
    data.starts_with(&SWAP_BASE_INPUT_DISCRIMINATOR)
        || data.starts_with(&SWAP_BASE_OUTPUT_DISCRIMINATOR)
}

pub fn decode_pool(
    owner_program: &str,
    data: &[u8],
) -> Result<RaydiumCpmmPoolState, RaydiumCpmmDecodeError> {
    if owner_program != PROGRAM_ID {
        return Err(RaydiumCpmmDecodeError::WrongOwner);
    }
    if data.len() != POOL_ACCOUNT_LEN {
        return Err(RaydiumCpmmDecodeError::WrongLength);
    }
    if data[..8] != POOL_DISCRIMINATOR {
        return Err(RaydiumCpmmDecodeError::WrongDiscriminator);
    }

    Ok(RaydiumCpmmPoolState {
        token_0_vault: pubkey(data, 72),
        token_1_vault: pubkey(data, 104),
        token_0_mint: pubkey(data, 168),
        token_1_mint: pubkey(data, 200),
        token_0_program: pubkey(data, 232),
        token_1_program: pubkey(data, 264),
        mint_0_decimals: data[331],
        mint_1_decimals: data[332],
        protocol_fees_token_0: u64_at(data, 341),
        protocol_fees_token_1: u64_at(data, 349),
        fund_fees_token_0: u64_at(data, 357),
        fund_fees_token_1: u64_at(data, 365),
        creator_fees_token_0: u64_at(data, 397),
        creator_fees_token_1: u64_at(data, 405),
    })
}

fn pubkey(data: &[u8], offset: usize) -> String {
    bs58::encode(&data[offset..offset + 32]).into_string()
}

fn u64_at(data: &[u8], offset: usize) -> u64 {
    u64::from_le_bytes(
        data[offset..offset + 8]
            .try_into()
            .expect("validated layout"),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decodes_canonical_packed_pool_offsets() {
        let mut data = vec![0_u8; POOL_ACCOUNT_LEN];
        data[..8].copy_from_slice(&POOL_DISCRIMINATOR);
        data[72..104].copy_from_slice(&[1; 32]);
        data[104..136].copy_from_slice(&[2; 32]);
        data[168..200].copy_from_slice(&[3; 32]);
        data[200..232].copy_from_slice(&[4; 32]);
        data[232..264].copy_from_slice(&[5; 32]);
        data[264..296].copy_from_slice(&[6; 32]);
        data[331] = 6;
        data[332] = 9;
        for (offset, value) in [
            (341, 11_u64),
            (349, 12),
            (357, 13),
            (365, 14),
            (397, 15),
            (405, 16),
        ] {
            data[offset..offset + 8].copy_from_slice(&value.to_le_bytes());
        }

        let state = decode_pool(PROGRAM_ID, &data).unwrap();
        assert_eq!(state.token_0_mint, bs58::encode([3_u8; 32]).into_string());
        assert_eq!(state.token_1_vault, bs58::encode([2_u8; 32]).into_string());
        assert_eq!(state.mint_0_decimals, 6);
        assert_eq!(state.creator_fees_token_1, 16);
    }

    #[test]
    fn rejects_similar_accounts_with_the_wrong_size_or_owner() {
        let mut data = vec![0_u8; POOL_ACCOUNT_LEN];
        data[..8].copy_from_slice(&POOL_DISCRIMINATOR);
        assert_eq!(
            decode_pool("wrong", &data),
            Err(RaydiumCpmmDecodeError::WrongOwner)
        );
        data.push(0);
        assert_eq!(
            decode_pool(PROGRAM_ID, &data),
            Err(RaydiumCpmmDecodeError::WrongLength)
        );
    }
}
