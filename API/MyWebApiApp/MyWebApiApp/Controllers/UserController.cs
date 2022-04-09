﻿using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MyWebApiApp.Data;
using MyWebApiApp.Models;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
namespace MyWebApiApp.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UserController : ControllerBase
    {
        private readonly MyDbContext _context;
        private readonly AppSetting _appSettings;

        public UserController(MyDbContext context, IOptionsMonitor<AppSetting> optionsMonitor)
        {
            _context = context;
            _appSettings = optionsMonitor.CurrentValue;
        }

        [HttpPost("Login")]
        [AllowAnonymous]
        public async Task<IActionResult> Validate(LoginModel model)
        {
            var user = _context.NguoiDungs.SingleOrDefault(p => p.UserName == model.UserName && model.Password == p.Password);
            if (user == null) 
            {
                return Ok(new ApiResponse
                {
                    Success = false,
                    Message = "Invalid username/password"
                });
            }

            //cap token
            var token = await GenerateToken(user);

            return Ok(new ApiResponse
            {
                Success = true,
                Message = "Authenticate success",
                Data = token
            });
        }

        private async Task<TokenModel> GenerateToken(NguoiDung nguoiDung)
        {
            var jwtTokenHandler = new JwtSecurityTokenHandler();

            var secretKeyBytes = Encoding.UTF8.GetBytes(_appSettings.SecretKey);

            var tokenDescription = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[] {
                    new Claim(ClaimTypes.Name, nguoiDung.HoTen),
                    new Claim(ClaimTypes.Role, nguoiDung.Role),
                    new Claim(JwtRegisteredClaimNames.Email, nguoiDung.Email),
                    new Claim(JwtRegisteredClaimNames.Sub, nguoiDung.Email),
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                    new Claim("UserName", nguoiDung.UserName),
                    new Claim("Id", nguoiDung.Id.ToString()),

                    //roles
                }),
                Expires = DateTime.UtcNow.AddSeconds(60),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(secretKeyBytes), SecurityAlgorithms.HmacSha512Signature)
            };

            var token = jwtTokenHandler.CreateToken(tokenDescription);
            var accessToken = jwtTokenHandler.WriteToken(token);
            var refreshToken = GenerateRefreshToken();

            //Save to database
            var refreshTokenEntity = new RefreshToken
            {
                Id = Guid.NewGuid(),
                JwtId = token.Id,
                UserId = nguoiDung.Id,
                Token = refreshToken,
                IsUsed = false,
                IsRevoked = false,
                IssuedAt = DateTime.UtcNow,
                ExpiredAt = DateTime.UtcNow.AddHours(1)
            };

            await _context.AddAsync(refreshTokenEntity);
            await _context.SaveChangesAsync();

            return new TokenModel
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken
            };
        }

        private string GenerateRefreshToken()
        {
            var random = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(random);

                return Convert.ToBase64String(random);
            }
        }

        [HttpPost("RenewToken")]
        public async Task<IActionResult> RenewToken(TokenModel model)
        {
            var jwtTokenHandler = new JwtSecurityTokenHandler();
            var secretKeyBytes = Encoding.UTF8.GetBytes(_appSettings.SecretKey);
            var tokenValidateParam = new TokenValidationParameters
            {
                //tu cap token
                ValidateIssuer = false,
                ValidateAudience = false,

                //ky vao token
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(secretKeyBytes),

                ClockSkew = TimeSpan.Zero,

                ValidateLifetime = false //ko kiểm tra token hết hạn
            };
            try
            {
                //check 1: AccessToken valid format
                var tokenInVerification = jwtTokenHandler.ValidateToken(model.AccessToken, tokenValidateParam, out var validatedToken);

                //check 2: Check alg
                if (validatedToken is JwtSecurityToken jwtSecurityToken)
                {
                    var result = jwtSecurityToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha512, StringComparison.InvariantCultureIgnoreCase);
                    if (!result)//false
                    {
                        return Ok(new ApiResponse
                        {
                            Success = false,
                            Message = "Invalid token"
                        });
                    }
                }

                //check 3: Check accessToken expire?
                var utcExpireDate = long.Parse(tokenInVerification.Claims.FirstOrDefault(x => x.Type == JwtRegisteredClaimNames.Exp).Value);

                var expireDate = ConvertUnixTimeToDateTime(utcExpireDate);
                if (expireDate > DateTime.UtcNow)
                {
                    return Ok(new ApiResponse
                    {
                        Success = false,
                        Message = "Access token has not yet expired"
                    });
                }

                //check 4: Check refreshtoken exist in DB
                var storedToken = _context.RefreshTokens.FirstOrDefault(x => x.Token == model.RefreshToken);
                if(storedToken == null)
                {
                    return Ok(new ApiResponse
                    {
                        Success = false,
                        Message = "Refresh token does not exist"
                    });
                }

                //check 5: check refreshToken is used/revoked?
                if (storedToken.IsUsed)
                {
                    return Ok(new ApiResponse
                    {
                        Success = false,
                        Message = "Refresh token has been used"
                    });
                }
                if (storedToken.IsRevoked)
                {
                    return Ok(new ApiResponse
                    {
                        Success = false,
                        Message = "Refresh token has been revoked"
                    });
                }

                //check 6: AccessToken id == JwtId in RefreshToken
                var jti = tokenInVerification.Claims.FirstOrDefault(x => x.Type == JwtRegisteredClaimNames.Jti).Value;
                if(storedToken.JwtId != jti)
                {
                    return Ok(new ApiResponse
                    {
                        Success = false,
                        Message = "Token doesn't match"
                    });
                }

                //Update token is used
                storedToken.IsRevoked = true;
                storedToken.IsUsed = true;
                _context.Update(storedToken);
                await _context.SaveChangesAsync();

                // ok all
                //create new token
                var user = await _context.NguoiDungs.SingleOrDefaultAsync(nd => nd.Id == storedToken.UserId);
                var token = await GenerateToken(user);

                return Ok(new ApiResponse
                {
                    Success = true,
                    Message = "Renew token success",
                    Data = token
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new ApiResponse
                {
                    Success = false,
                    Message = "Something went wrong"
                });
            }
        }

        private DateTime ConvertUnixTimeToDateTime(long utcExpireDate)
        {
            var dateTimeInterval = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            dateTimeInterval.AddSeconds(utcExpireDate).ToUniversalTime();

            return dateTimeInterval;
        }

        [HttpPost("Register_For_Admin")]
        [Authorize(Roles = "admin")]
        public IActionResult CreateNew(RegisterModel model)
        {
            try
            {
                var nguoidung = new NguoiDung
                {
                    UserName = model.UserName,
                    Password = model.Password,
                    HoTen = model.HoTen,
                    Email = model.Email,
                    Role = model.Role
                };
                _context.Add(nguoidung);
                _context.SaveChanges();
                return StatusCode(StatusCodes.Status201Created, nguoidung);
            }
            catch
            {
                return BadRequest();
            }
        }

        [HttpPost("Register_For_Guest")]
        [AllowAnonymous]
        public IActionResult CreateNew2(RegisterUserModel model)
        {
            try
            {
                var nguoidung = new NguoiDung
                {
                    UserName = model.UserName,
                    Password = model.Password,
                    HoTen = model.HoTen,
                    Email = model.Email,
                    Role = "user"
                };
                _context.Add(nguoidung);
                _context.SaveChanges();
                return StatusCode(StatusCodes.Status201Created, nguoidung);
            }
            catch
            {
                return BadRequest();
            }
        }


        [HttpGet]
        [Authorize(Roles = "admin")]
        public IActionResult GetAll()
        {
            try
            {
                var dsNguoiDung = _context.NguoiDungs.ToList();
                return Ok(dsNguoiDung);
            }
            catch
            {
                return BadRequest();
            }
        }

        [HttpGet("{username}")]
        [Authorize(Roles = "admin")]
        public IActionResult GetById(string username)
        {
            var ngdung = _context.NguoiDungs.SingleOrDefault(ng => ng.UserName == username);
            if (ngdung != null)
            {
                return Ok(ngdung);
            }
            else
            {
                return NotFound();
            }
        }

        [HttpPut("{username}")]
        [Authorize(Roles = "admin")]
        public IActionResult UpdateLoaiById(string username, RegisterModel model)
        {
            var ngdung = _context.NguoiDungs.SingleOrDefault(user => user.UserName == username);
            if (ngdung != null)
            {
                ngdung.UserName = model.UserName;
                ngdung.Password = model.Password;
                ngdung.Email = model.Email;
                ngdung.HoTen = model.HoTen;
                ngdung.Role = model.Role;
                _context.SaveChanges();
                return NoContent();
            }
            else
            {
                return NotFound();
            }
        }

        [HttpDelete("username")]
        [Authorize(Roles = "admin")]
        public IActionResult DeleteLoaiById(string username)
        {
            var ngdung = _context.NguoiDungs.SingleOrDefault(user => user.UserName == username);
            if (ngdung != null)
            {
                _context.Remove(ngdung);
                _context.SaveChanges();
                return StatusCode(StatusCodes.Status200OK);
            }
            else
            {
                return NotFound();
            }
        }
    }
}
