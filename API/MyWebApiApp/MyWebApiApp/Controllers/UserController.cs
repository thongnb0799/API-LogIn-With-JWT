using Microsoft.AspNetCore.Http;
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
using HangHoa = MyWebApiApp.Models.HangHoa;

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
        public IActionResult Validate(LoginModel model)
        {
            var user = _context.NguoiDungs.SingleOrDefault(p => p.UserName == model.UserName && model.Password == p.Password);
            if (user == null) //không đúng
            {
                return Ok(new ApiResponse
                {
                    Success = false,
                    Message = "Invalid username/password"
                });
            }

            //cấp token

            return Ok(new ApiResponse
            {
                Success = true,
                Message = "Authenticate success",
                Data = GenerateToken(user)
            });
        }

        private string GenerateToken(NguoiDung nguoiDung)
        {
            var jwtTokenHandler = new JwtSecurityTokenHandler();

            var secretKeyBytes = Encoding.UTF8.GetBytes(_appSettings.SecretKey);

            var tokenDescription = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[] { 
                    new Claim(ClaimTypes.Name, nguoiDung.HoTen),
                    new Claim(ClaimTypes.Email, nguoiDung.Email),
                    new Claim("UserName", nguoiDung.UserName),
                    new Claim("Id", nguoiDung.Id.ToString()),

                    //roles

                    new Claim("TokenId", Guid.NewGuid().ToString())
                }),
                Expires = DateTime.UtcNow.AddMinutes(1),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(secretKeyBytes), SecurityAlgorithms.HmacSha512Signature)
            };

            var token = jwtTokenHandler.CreateToken(tokenDescription);

            return jwtTokenHandler.WriteToken(token);
        }

      
      [HttpPost("Register")]    
      public IActionResult CreateNew(RegisterModel model)
        {
            try
            {
                var nguoidung = new NguoiDung
                {
                     UserName = model.UserName,
                     Password = model.Password,
                     HoTen = model.HoTen,
                     Email = model.Email
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
        public IActionResult UpdateLoaiById(string username, RegisterModel model)
        {
            var ngdung = _context.NguoiDungs.SingleOrDefault(user => user.UserName == username);
            if (ngdung != null)
            {
                ngdung.UserName = model.UserName;
                ngdung.Password = model.Password;
                ngdung.Email = model.Email;
                ngdung.HoTen = model.HoTen;
                _context.SaveChanges();
                return NoContent();
            }
            else
            {
                return NotFound();
            }
        }

        [HttpDelete("username")]
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
